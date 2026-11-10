using System;
using System.Collections.Generic;
using System.Linq;
using AnimatorAsCode.V1;
using UnityEditor.Animations;
using UnityEngine;

namespace FaceExpressionFramework.Editor
{
    /// <summary>Signal演算で生成される内部Animator Parameterです。</summary>
    internal sealed class ParameterSignal
    {
        private readonly Func<AacFlFloatParameter, Motion> _motionFactory;

        internal ParameterSignal(
            AacFlFloatParameter parameter,
            Func<AacFlFloatParameter, Motion> motionFactory)
        {
            Parameter = parameter;
            _motionFactory = motionFactory;
        }

        internal AacFlFloatParameter Parameter { get; }

        internal Func<AacFlFloatParameter, Motion> MotionFactory => _motionFactory;
    }

    /// <summary>
    /// 入力と派生Signalを依存順にAnimatorへ生成します。
    /// 入力の意味と名前はアバター側で定義します。
    /// </summary>
    public sealed class ParameterSignalGraph
    {
        private readonly FaceBuildContext _context;
        private readonly List<Motion> _signals = new List<Motion>();
        private readonly Dictionary<string, AacFlFloatParameter> _outputs =
            new Dictionary<string, AacFlFloatParameter>(StringComparer.Ordinal);
        private readonly List<IntEqualityGate> _intEqualityGates = new List<IntEqualityGate>();
        private readonly List<Action> _orderedOperations = new List<Action>();
        private int _nextSignalId;
        private AacFlLayer _layer;
        private AacFlFloatParameter _one;
        private bool _built;
        private string _rootLayerName;

        internal ParameterSignalGraph(
            FaceBuildContext context,
            SmoothingBuilder smoothing)
        {
            _context = context;
            Smoothing = smoothing;
        }

        internal SmoothingBuilder Smoothing { get; }
        internal string RootLayerName => _rootLayerName;
        internal AacFlFloatParameter One
        {
            get
            {
                EnsureLayer();
                return _one;
            }
        }

        public RawSignal Input(string parameterName) =>
            new RawSignal(this, RawInput(parameterName));

        public UnitSignal UnitInput(string parameterName) =>
            Input(parameterName).ToUnit(0f, 1f);

        public void OverrideInputDefault(string parameterName, float value)
        {
            var input = InputParameter(parameterName);
            _layer.OverrideValue(input, value);
        }

        internal string NextSignalName(string operation) => operation + "/" + ++_nextSignalId;

        internal ParameterSignal RawInput(string parameterName)
        {
            var input = InputParameter(parameterName);
            Func<AacFlFloatParameter, Motion> factory = target =>
                CreateMap(input, target, -1024f, -1024f, 1024f, 1024f, parameterName);
            return new ParameterSignal(input, factory);
        }

        internal ParameterSignal SmoothInput(ParameterSignal source, float localSmoothness, float remoteSmoothness)
        {
            EnsureNotBuilt();
            var output = Smoothing.Add(source.Parameter, localSmoothness, remoteSmoothness);
            Func<AacFlFloatParameter, Motion> factory = target =>
                CreateMap(output, target, -1024f, -1024f, 1024f, 1024f, output.Name);
            return new ParameterSignal(output, factory);
        }

        internal ParameterSignal MapSignal(
            ParameterSignal source, string outputName,
            float inputA, float outputA, float inputB, float outputB)
        {
            EnsureNotBuilt();
            if (source == null) throw new ArgumentNullException(nameof(source));
            var output = Output(outputName);
            if (inputA > inputB)
            {
                var oldInput = inputA;
                var oldOutput = outputA;
                inputA = inputB;
                outputA = outputB;
                inputB = oldInput;
                outputB = oldOutput;
            }
            var sourceIsDerived = _outputs.ContainsKey(source.Parameter.Name);
            if (!sourceIsDerived)
            {
                // 外部入力は基礎レイヤーを共有できる。派生入力は計算結果を読むため後続レイヤーへ配置する。
                _signals.Add(CreateMap(
                    source.Parameter, output,
                    inputA, outputA, inputB, outputB, outputName));
            }
            else
            {
                _orderedOperations.Add(() => BuildMapLayer(
                    "Map " + outputName, source.Parameter, output,
                    inputA, outputA, inputB, outputB));
            }
            return Derived(output, outputName);
        }

        /// <summary>2つの正規化Signalの正確な最大値を返します。</summary>
        internal ParameterSignal Max(string outputName, ParameterSignal left, ParameterSignal right)
        {
            EnsureNotBuilt();
            if (left == null) throw new ArgumentNullException(nameof(left));
            if (right == null) throw new ArgumentNullException(nameof(right));
            var output = Output(outputName);
            var operation = new MaxOperation(outputName, left.Parameter, right.Parameter, output);
            _orderedOperations.Add(() => BuildMax(operation));
            return Derived(output, outputName);
        }

        /// <summary>
        /// normalを正側、priorityを負側に配置した符号付き軸を生成します。
        /// priorityの増加に応じてnormalを連続的に置き換え、priorityが1なら常に-1になります。
        /// </summary>
        internal ParameterSignal Prioritize(
            string outputName,
            ParameterSignal normal,
            ParameterSignal priority)
        {
            EnsureNotBuilt();
            if (normal == null) throw new ArgumentNullException(nameof(normal));
            if (priority == null) throw new ArgumentNullException(nameof(priority));
            var output = Output(outputName);
            var operation = new PriorityOperation(
                outputName, normal.Parameter, priority.Parameter, output);
            _orderedOperations.Add(() => BuildPriority(operation));
            return Derived(output, outputName);
        }

        /// <summary>符号付きSignalを非負の下側と上側へ分割します。</summary>
        internal (ParameterSignal Lower, ParameterSignal Upper) Split(
            ParameterSignal source, string lowerName, string upperName)
        {
            EnsureNotBuilt();
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(lowerName))
                throw new ArgumentException("A lower-side name is required.", nameof(lowerName));
            if (string.IsNullOrWhiteSpace(upperName))
                throw new ArgumentException("An upper-side name is required.", nameof(upperName));

            var lower = Output(lowerName);
            var upper = Output(upperName);
            var operation = new SplitOperation(
                source.Parameter, lowerName, lower, upperName, upper);
            _orderedOperations.Add(() => BuildSplit(operation));
            return (
                Derived(lower, lowerName),
                Derived(upper, upperName));
        }

        internal ParameterSignal Branch(
            ParameterSignal source,
            string outputName,
            IReadOnlyList<ParameterSuppression> suppressions)
        {
            EnsureNotBuilt();
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(outputName))
                throw new ArgumentException("A branch name is required.", nameof(outputName));
            if (suppressions == null) throw new ArgumentNullException(nameof(suppressions));
            if (suppressions.Count == 0)
                throw new ArgumentException("At least one suppression rule is required.", nameof(suppressions));

            var mappings = new List<SuppressionMapOperation>(suppressions.Count);
            for (var index = 0; index < suppressions.Count; index++)
            {
                var suppression = suppressions[index];
                if (suppression.Condition == null)
                    throw new ArgumentException("Suppression conditions cannot contain null.", nameof(suppressions));
                ValidateSuppressionRange(suppression.StartsAt, suppression.FullyAt);

                var amount = Output(outputName + "/Suppression/" + index);
                mappings.Add(new SuppressionMapOperation(
                    suppression.Condition.Parameter, amount,
                    suppression.StartsAt, suppression.FullyAt));
            }

            var strongest = mappings.Count == 1 ? mappings[0].Output : Output(outputName + "/Suppression/Maximum");

            var output = Output(outputName);
            Func<AacFlFloatParameter, Motion> factory = target =>
                GateOffWhenPositive(
                    source.MotionFactory(target),
                    strongest,
                    target,
                    outputName);
            var operation = new BranchOperation(
                outputName, output, strongest, factory(output), mappings);
            _orderedOperations.Add(() => BuildBranch(operation));
            return new ParameterSignal(output, factory);
        }

        internal ParameterSignal WhenIntEquals(
            ParameterSignal source,
            string outputName,
            string parameterName,
            int expectedValue)
        {
            EnsureNotBuilt();
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(outputName))
                throw new ArgumentException("A branch name is required.", nameof(outputName));
            if (string.IsNullOrWhiteSpace(parameterName))
                throw new ArgumentException("A condition parameter name is required.", nameof(parameterName));

            var output = Output(outputName);
            var operation = new IntEqualityGate(
                outputName,
                source.Parameter,
                parameterName,
                expectedValue,
                output);
            _intEqualityGates.Add(operation);
            _orderedOperations.Add(() => BuildIntEqualityGate(operation));
            return Derived(output, outputName);
        }

        internal void Build()
        {
            EnsureNotBuilt();
            _built = true;
            if (_layer == null) return;
            Motion rootMotion;
            if (_signals.Count == 0)
            {
                rootMotion = Constant(One, 1f, "Signals One");
            }
            else
            {
                var root = NewTree("Signals");
                root.blendType = BlendTreeType.Direct;
                root.blendParameter = One.Name;
                root.children = _signals.Select(signal => new ChildMotion
                {
                    motion = signal,
                    directBlendParameter = One.Name,
                    timeScale = 1f
                }).ToArray();
                // Direct Treeで疎なAAP出力を合成するため、兄弟MotionのWeightを正規化する。
                var serializedRoot = new UnityEditor.SerializedObject(root);
                serializedRoot.FindProperty("m_NormalizedBlendValues").boolValue = true;
                serializedRoot.ApplyModifiedPropertiesWithoutUndo();
                ScaleSignalOutputs(_signals.Count);
                rootMotion = root;
            }
            var state = _layer.NewState("Evaluate")
                .WithAnimation(rootMotion)
                .WithWriteDefaultsSetTo(false);
            _layer.WithDefaultState(state);

            foreach (var operation in _orderedOperations)
                operation();
        }

        private ParameterSignal Derived(AacFlFloatParameter output, string label)
        {
            Func<AacFlFloatParameter, Motion> factory = target =>
                CreateMap(output, target, 0f, 0f, 1f, 1f, label);
            return new ParameterSignal(output, factory);
        }

        private void BuildIntEqualityGate(IntEqualityGate gate)
        {
            var layer = _context.Controller.NewLayer("Condition " + gate.Label)
                .WithAvatarMaskNoTransforms();
            var condition = layer.IntParameter(gate.ParameterName);
            layer.FloatParameter(gate.Source.Name);
            layer.FloatParameter(gate.Output.Name);

            var inactive = layer.NewState("Inactive")
                .WithAnimation(Constant(gate.Output, 0f, gate.Label + " inactive"))
                .WithWriteDefaultsSetTo(false);
            var active = layer.NewState("Active")
                .WithAnimation(CreateMap(gate.Source, gate.Output, 0f, 0f, 1f, 1f, gate.Label))
                .WithWriteDefaultsSetTo(false);
            layer.WithDefaultState(inactive);
            inactive.TransitionsTo(active)
                .WithTransitionDurationSeconds(0f)
                .When(condition.IsEqualTo(gate.ExpectedValue));
            active.TransitionsTo(inactive)
                .WithTransitionDurationSeconds(0f)
                .When(condition.IsNotEqualTo(gate.ExpectedValue));
        }

        private void BuildPriority(PriorityOperation operation)
        {
            var layer = _context.Controller.NewLayer("Prioritize " + operation.Label)
                .WithAvatarMaskNoTransforms();
            layer.FloatParameter(operation.Normal.Name);
            layer.FloatParameter(operation.Priority.Name);
            layer.FloatParameter(operation.Output.Name);

            var normal = CreateMap(
                operation.Normal,
                operation.Output,
                0f, 0f,
                1f, 1f,
                operation.Label + " normal");
            var tree = NewTree(operation.Label);
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = operation.Priority.Name;
            tree.useAutomaticThresholds = false;
            tree.children = new[]
            {
                new ChildMotion { motion = normal, threshold = 0f, timeScale = 1f },
                new ChildMotion
                {
                    motion = Constant(operation.Output, -1f, operation.Label + " priority"),
                    threshold = 1f,
                    timeScale = 1f
                }
            };
            var state = layer.NewState("Evaluate")
                .WithAnimation(tree)
                .WithWriteDefaultsSetTo(false);
            layer.WithDefaultState(state);
        }

        private void BuildSplit(SplitOperation operation)
        {
            BuildMapLayer(
                "Split " + operation.LowerLabel,
                operation.Source,
                operation.Lower,
                -1f, 1f, 0f, 0f);
            BuildMapLayer(
                "Split " + operation.UpperLabel,
                operation.Source,
                operation.Upper,
                0f, 0f, 1f, 1f);
        }

        private void BuildMapLayer(
            string label,
            AacFlFloatParameter source,
            AacFlFloatParameter output,
            float fromA,
            float toA,
            float fromB,
            float toB)
        {
            var layer = _context.Controller.NewLayer(label)
                .WithAvatarMaskNoTransforms();
            layer.FloatParameter(source.Name);
            layer.FloatParameter(output.Name);
            var state = layer.NewState("Evaluate")
                .WithAnimation(CreateMap(source, output, fromA, toA, fromB, toB, label))
                .WithWriteDefaultsSetTo(false);
            layer.WithDefaultState(state);
        }

        private void BuildMax(MaxOperation operation)
        {
            var difference = _context.Controller.NewLayer("Max Difference " + operation.Label)
                .WithAvatarMaskNoTransforms();
            difference.FloatParameter(operation.Left.Name);
            difference.FloatParameter(operation.Right.Name);
            var delta = difference.FloatParameter(_context.InternalName("Max Difference/" + operation.Label));
            var leftSnapshot = difference.FloatParameter(_context.InternalName("Max Snapshot/Left/" + operation.Label));
            var rightSnapshot = difference.FloatParameter(_context.InternalName("Max Snapshot/Right/" + operation.Label));
            var leftDefault = _context.Controller.AnimatorController.parameters.First(parameter => parameter.name == operation.Left.Name).defaultFloat;
            var rightDefault = _context.Controller.AnimatorController.parameters.First(parameter => parameter.name == operation.Right.Name).defaultFloat;
            difference.OverrideValue(leftSnapshot, leftDefault);
            difference.OverrideValue(rightSnapshot, rightDefault);
            difference.OverrideValue(delta, leftDefault - rightDefault);
            var leftClip = Constant(delta, 1f, operation.Label + " left");
            var rightClip = Constant(delta, -1f, operation.Label + " right");
            // 判定と同時に左右値を保存し、次の評価では同じ時点の値を選ぶ。
            leftClip.SetCurve(string.Empty, typeof(Animator), leftSnapshot.Name, new AnimationCurve(new Keyframe(0f, 1f)));
            leftClip.SetCurve(string.Empty, typeof(Animator), rightSnapshot.Name, new AnimationCurve(new Keyframe(0f, 0f)));
            rightClip.SetCurve(string.Empty, typeof(Animator), leftSnapshot.Name, new AnimationCurve(new Keyframe(0f, 0f)));
            rightClip.SetCurve(string.Empty, typeof(Animator), rightSnapshot.Name, new AnimationCurve(new Keyframe(0f, 1f)));
            var differenceTree = NewTree(operation.Label + " difference");
            differenceTree.blendType = BlendTreeType.Direct;
            differenceTree.children = new[]
            {
                new ChildMotion
                {
                    motion = leftClip,
                    directBlendParameter = operation.Left.Name,
                    timeScale = 1f
                },
                new ChildMotion
                {
                    motion = rightClip,
                    directBlendParameter = operation.Right.Name,
                    timeScale = 1f
                }
            };
            var differenceState = difference.NewState("Evaluate")
                .WithAnimation(differenceTree)
                .WithWriteDefaultsSetTo(false);
            difference.WithDefaultState(differenceState);

            var select = _context.Controller.NewLayer("Max " + operation.Label)
                .WithAvatarMaskNoTransforms();
            select.FloatParameter(operation.Output.Name);
            select.FloatParameter(leftSnapshot.Name);
            select.FloatParameter(rightSnapshot.Name);
            select.OverrideValue(operation.Output, Mathf.Max(leftDefault, rightDefault));
            var comparison = select.FloatParameter(delta.Name);
            var left = select.NewState("Left")
                .WithAnimation(CreateMap(leftSnapshot, operation.Output, 0f, 0f, 1f, 1f, operation.Label + " left"))
                .WithWriteDefaultsSetTo(false);
            var right = select.NewState("Right")
                .WithAnimation(CreateMap(rightSnapshot, operation.Output, 0f, 0f, 1f, 1f, operation.Label + " right"))
                .WithWriteDefaultsSetTo(false);
            select.WithDefaultState(leftDefault >= rightDefault ? left : right);
            left.TransitionsTo(right)
                .WithTransitionDurationSeconds(0f)
                .When(comparison.IsLessThan(0f));
            right.TransitionsTo(left)
                .WithTransitionDurationSeconds(0f)
                .When(comparison.IsGreaterThan(0f));
        }

        private void BuildBranch(BranchOperation branch)
        {
            // 条件Signalは前段レイヤーの派生値も使うため、完成した値から抑制を計算する。
            // 最初のParameter Logicレイヤーで生入力から計算してはならない。
            for (var index = 0; index < branch.Mappings.Count; index++)
            {
                var mapping = branch.Mappings[index];
                BuildMapLayer(
                    "Suppression Amount " + branch.Label + "/" + index,
                    mapping.Source, mapping.Output,
                    mapping.StartsAt, 0f, mapping.FullyAt, 1f);
            }
            if (branch.Mappings.Count > 1)
                BuildSuppressionMaximum(branch);

            var layer = _context.Controller.NewLayer("Suppress " + branch.Label)
                .WithAvatarMaskNoTransforms();
            layer.FloatParameter(branch.Output.Name);
            layer.FloatParameter(branch.Suppression.Name);
            var state = layer.NewState("Evaluate")
                .WithAnimation(branch.Motion)
                .WithWriteDefaultsSetTo(false);
            layer.WithDefaultState(state);
        }

        private void BuildSuppressionMaximum(BranchOperation branch)
        {
            var inputs = branch.Mappings.Select(mapping => mapping.Output).ToArray();
            var capture = _context.Controller.NewLayer("Suppression Capture " + branch.Label)
                .WithAvatarMaskNoTransforms();
            var snapshots = inputs.Select((input, index) => capture.FloatParameter(
                _context.InternalName("Suppression Snapshot/" + branch.Label + "/" + index))).ToArray();
            var inputDefaults = inputs.Select(input => _context.Controller.AnimatorController.parameters
                .First(parameter => parameter.name == input.Name).defaultFloat).ToArray();
            for (var index = 0; index < snapshots.Length; index++)
                capture.OverrideValue(snapshots[index], inputDefaults[index]);
            var comparisons = new AacFlFloatParameter[inputs.Length, inputs.Length];
            for (var left = 0; left < inputs.Length; left++)
            for (var right = 0; right < inputs.Length; right++)
                if (left != right)
                {
                    comparisons[left, right] = capture.FloatParameter(_context.InternalName(
                        "Suppression Difference/" + branch.Label + "/" + left + "/" + right));
                    capture.OverrideValue(comparisons[left, right], inputDefaults[left] - inputDefaults[right]);
                }

            // 全条件を同じ評価で保存する。Maxを直列にすると条件ごとの時点がずれる。
            var tree = NewTree(branch.Label + " suppression capture");
            tree.blendType = BlendTreeType.Direct;
            var children = new List<ChildMotion>();
            for (var inputIndex = 0; inputIndex < inputs.Length; inputIndex++)
            {
                capture.FloatParameter(inputs[inputIndex].Name);
                var clip = Constant(snapshots[inputIndex], 1f, branch.Label + " capture " + inputIndex);
                for (var snapshotIndex = 0; snapshotIndex < inputs.Length; snapshotIndex++)
                    clip.SetCurve(string.Empty, typeof(Animator), snapshots[snapshotIndex].Name,
                        new AnimationCurve(new Keyframe(0f, inputIndex == snapshotIndex ? 1f : 0f)));
                for (var left = 0; left < inputs.Length; left++)
                for (var right = 0; right < inputs.Length; right++)
                    if (left != right)
                        clip.SetCurve(string.Empty, typeof(Animator), comparisons[left, right].Name,
                            new AnimationCurve(new Keyframe(0f,
                                inputIndex == left ? 1f : inputIndex == right ? -1f : 0f)));
                children.Add(new ChildMotion { motion = clip, directBlendParameter = inputs[inputIndex].Name, timeScale = 1f });
            }
            tree.children = children.ToArray();
            capture.WithDefaultState(capture.NewState("Capture").WithAnimation(tree).WithWriteDefaultsSetTo(false));

            var select = _context.Controller.NewLayer("Suppression Maximum " + branch.Label)
                .WithAvatarMaskNoTransforms();
            var states = snapshots.Select((snapshot, index) =>
            {
                select.FloatParameter(snapshot.Name);
                return select.NewState("Condition " + index).WithAnimation(CreateMap(
                    snapshot, branch.Suppression, 0f, 0f, 1f, 1f, branch.Label + " maximum " + index))
                    .WithWriteDefaultsSetTo(false);
            }).ToArray();
            select.FloatParameter(branch.Suppression.Name);
            var initialMaximum = inputDefaults.Max();
            select.OverrideValue(branch.Suppression, initialMaximum);
            select.WithDefaultState(states[Array.IndexOf(inputDefaults, initialMaximum)]);
            for (var source = 0; source < inputs.Length; source++)
            for (var destination = 0; destination < inputs.Length; destination++)
            {
                if (source == destination) continue;
                select.FloatParameter(comparisons[destination, source].Name);
                var transition = states[source].TransitionsTo(states[destination])
                    .WithTransitionDurationSeconds(0f)
                    .When(comparisons[destination, source].IsGreaterThan(0f));
                for (var other = 0; other < inputs.Length; other++)
                {
                    if (other == source || other == destination) continue;
                    select.FloatParameter(comparisons[destination, other].Name);
                    // floatには0と-Epsilonの間の値がないため、この条件は0以上を表す。
                    transition.And(comparisons[destination, other].IsGreaterThan(-float.Epsilon));
                }
            }
        }

        private void ScaleSignalOutputs(float factor)
        {
            var outputNames = new HashSet<string>(_outputs.Keys, StringComparer.Ordinal);
            foreach (var signal in _signals)
            foreach (var clip in EnumerateClips(signal))
            foreach (var binding in UnityEditor.AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type != typeof(Animator) || !outputNames.Contains(binding.propertyName)) continue;
                var curve = UnityEditor.AnimationUtility.GetEditorCurve(clip, binding);
                var keys = curve.keys;
                for (var index = 0; index < keys.Length; index++)
                {
                    keys[index].value *= factor;
                    keys[index].inTangent *= factor;
                    keys[index].outTangent *= factor;
                }
                curve.keys = keys;
                UnityEditor.AnimationUtility.SetEditorCurve(clip, binding, curve);
            }
        }

        private static IEnumerable<AnimationClip> EnumerateClips(Motion motion)
        {
            if (motion is AnimationClip clip)
            {
                yield return clip;
                yield break;
            }
            if (!(motion is BlendTree tree)) yield break;
            foreach (var child in tree.children)
            foreach (var nested in EnumerateClips(child.motion))
                yield return nested;
        }

        private AacFlFloatParameter InputParameter(string name)
        {
            EnsureNotBuilt();
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An input parameter name is required.", nameof(name));
            EnsureLayer();
            return _layer.FloatParameter(name);
        }

        private AacFlFloatParameter Output(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An output name is required.", nameof(name));
            EnsureLayer();
            var parameterName = _context.InternalName("Coefficient/" + name);
            if (_outputs.TryGetValue(parameterName, out var existing)) return existing;
            var output = _layer.FloatParameter(parameterName);
            _outputs.Add(parameterName, output);
            return output;
        }

        private void EnsureLayer()
        {
            if (_layer != null) return;
            _layer = _context.Controller.NewLayer("Parameter Logic")
                .WithAvatarMaskNoTransforms();
            _rootLayerName = _context.Controller.AnimatorController.layers.Last().name;
            _one = _layer.FloatParameter(_context.InternalName("One"));
            _layer.OverrideValue(_one, 1f);
        }

        private Motion GateOffWhenPositive(
            Motion active,
            AacFlFloatParameter condition,
            AacFlFloatParameter output,
            string label)
        {
            var tree = NewTree(label + " suppressed by " + condition.Name);
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = condition.Name;
            tree.useAutomaticThresholds = false;
            tree.children = new[]
            {
                new ChildMotion { motion = active, threshold = 0f, timeScale = 1f },
                new ChildMotion { motion = Constant(output, 0f, label + " suppressed"), threshold = 1f, timeScale = 1f }
            };
            return tree;
        }

        private Motion CreateMap(
            AacFlFloatParameter source,
            AacFlFloatParameter output,
            float fromA,
            float toA,
            float fromB,
            float toB,
            string label)
        {
            var tree = NewTree(label);
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = source.Name;
            tree.useAutomaticThresholds = false;
            tree.children = new[]
            {
                new ChildMotion { motion = Constant(output, toA, label + " A"), threshold = fromA, timeScale = 1f },
                new ChildMotion { motion = Constant(output, toB, label + " B"), threshold = fromB, timeScale = 1f }
            };
            return tree;
        }

        private AnimationClip Constant(AacFlFloatParameter output, float value, string label)
        {
            var clip = _context.Aac.NewClip(label).Clip;
            clip.SetCurve(string.Empty, typeof(Animator), output.Name,
                new AnimationCurve(new Keyframe(0f, value)));
            return clip;
        }

        private BlendTree NewTree(string name)
        {
            var tree = _context.Aac.NewBlendTreeAsRaw();
            tree.name = name;
            return tree;
        }

        private void EnsureNotBuilt()
        {
            if (_built) throw new InvalidOperationException("Parameter signals have already been built.");
        }

        private static void ValidateSuppressionRange(float startsAt, float fullyAt)
        {
            if (float.IsNaN(startsAt) || float.IsInfinity(startsAt) || startsAt < 0f || startsAt > 1f)
                throw new ArgumentOutOfRangeException(nameof(startsAt), "Suppression start must be in the 0..1 range.");
            if (float.IsNaN(fullyAt) || float.IsInfinity(fullyAt) || fullyAt < 0f || fullyAt > 1f)
                throw new ArgumentOutOfRangeException(nameof(fullyAt), "Full suppression must be in the 0..1 range.");
            if (startsAt >= fullyAt)
                throw new ArgumentException("Full suppression must be greater than the suppression start.");
        }

        private sealed class IntEqualityGate
        {
            internal IntEqualityGate(
                string label,
                AacFlFloatParameter source,
                string parameterName,
                int expectedValue,
                AacFlFloatParameter output)
            {
                Label = label;
                Source = source;
                ParameterName = parameterName;
                ExpectedValue = expectedValue;
                Output = output;
            }

            internal string Label { get; }
            internal AacFlFloatParameter Source { get; }
            internal string ParameterName { get; }
            internal int ExpectedValue { get; }
            internal AacFlFloatParameter Output { get; }
        }

        private sealed class MaxOperation
        {
            internal MaxOperation(
                string label,
                AacFlFloatParameter left,
                AacFlFloatParameter right,
                AacFlFloatParameter output)
            {
                Label = label;
                Left = left;
                Right = right;
                Output = output;
            }

            internal string Label { get; }
            internal AacFlFloatParameter Left { get; }
            internal AacFlFloatParameter Right { get; }
            internal AacFlFloatParameter Output { get; }
        }

        private sealed class PriorityOperation
        {
            internal PriorityOperation(
                string label,
                AacFlFloatParameter normal,
                AacFlFloatParameter priority,
                AacFlFloatParameter output)
            {
                Label = label;
                Normal = normal;
                Priority = priority;
                Output = output;
            }

            internal string Label { get; }
            internal AacFlFloatParameter Normal { get; }
            internal AacFlFloatParameter Priority { get; }
            internal AacFlFloatParameter Output { get; }
        }

        private sealed class SplitOperation
        {
            internal SplitOperation(
                AacFlFloatParameter source,
                string lowerLabel,
                AacFlFloatParameter lower,
                string upperLabel,
                AacFlFloatParameter upper)
            {
                Source = source;
                LowerLabel = lowerLabel;
                Lower = lower;
                UpperLabel = upperLabel;
                Upper = upper;
            }

            internal AacFlFloatParameter Source { get; }
            internal string LowerLabel { get; }
            internal AacFlFloatParameter Lower { get; }
            internal string UpperLabel { get; }
            internal AacFlFloatParameter Upper { get; }
        }

        private sealed class BranchOperation
        {
            internal BranchOperation(
                string label,
                AacFlFloatParameter output,
                AacFlFloatParameter suppression,
                Motion motion,
                IReadOnlyList<SuppressionMapOperation> mappings)
            {
                Label = label;
                Output = output;
                Suppression = suppression;
                Motion = motion;
                Mappings = mappings;
            }

            internal string Label { get; }
            internal AacFlFloatParameter Output { get; }
            internal AacFlFloatParameter Suppression { get; }
            internal Motion Motion { get; }
            internal IReadOnlyList<SuppressionMapOperation> Mappings { get; }
        }

        private sealed class SuppressionMapOperation
        {
            internal SuppressionMapOperation(
                AacFlFloatParameter source,
                AacFlFloatParameter output,
                float startsAt,
                float fullyAt)
            {
                Source = source;
                Output = output;
                StartsAt = startsAt;
                FullyAt = fullyAt;
            }

            internal AacFlFloatParameter Source { get; }
            internal AacFlFloatParameter Output { get; }
            internal float StartsAt { get; }
            internal float FullyAt { get; }
        }
    }

    internal readonly struct ParameterSuppression
    {
        internal ParameterSuppression(ParameterSignal condition, float startsAt, float fullyAt)
        {
            Condition = condition;
            StartsAt = startsAt;
            FullyAt = fullyAt;
        }

        internal ParameterSignal Condition { get; }
        internal float StartsAt { get; }
        internal float FullyAt { get; }
    }
}
