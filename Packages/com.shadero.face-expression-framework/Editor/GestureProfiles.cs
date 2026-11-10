using System;
using System.Collections.Generic;
using System.Linq;
using AnimatorAsCode.V1;
using AnimatorAsCode.V1.VRC;
using UnityEditor;
using UnityEngine;

namespace FaceExpressionFramework.Editor
{
    /// <summary>右手Gestureの設定です。</summary>
    public sealed class GestureProfileConfiguration
    {
        private readonly ExpressionProfileRegistry _profiles;
        private readonly Dictionary<AacAv3.Av3Gesture, ExpressionProfile> _gestureProfiles =
            new Dictionary<AacAv3.Av3Gesture, ExpressionProfile>();
        private readonly HashSet<AacAv3.Av3TrackingElement> _animatedTracking =
            new HashSet<AacAv3.Av3TrackingElement>();

        internal GestureProfileConfiguration(ExpressionProfileRegistry profiles) => _profiles = profiles;

        public ExpressionProfile DefaultProfile { get; private set; }
        public float TransitionDurationSeconds { get; private set; } = 0.15f;
        internal IEnumerable<AacAv3.Av3TrackingElement> AnimatedTracking => _animatedTracking;

        public GestureProfileConfiguration Default(ExpressionProfile profile)
        {
            DefaultProfile = profile ?? throw new ArgumentNullException(nameof(profile));
            return this;
        }

        public GestureProfileConfiguration TransitionDuration(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f)
                throw new ArgumentOutOfRangeException(nameof(seconds), "Transition duration must be zero or greater.");
            TransitionDurationSeconds = seconds;
            return this;
        }

        /// <summary>生成する全StateでVRChatの追跡要素をAnimation制御するよう指定します。</summary>
        public GestureProfileConfiguration TrackingAnimates(
            params AacAv3.Av3TrackingElement[] elements)
        {
            if (elements == null) throw new ArgumentNullException(nameof(elements));
            foreach (var element in elements) _animatedTracking.Add(element);
            return this;
        }

        public GestureProfileConfiguration For(
            AacAv3.Av3Gesture gesture,
            Action<ProfileDraft> configure)
        {
            EnsureGestureCanBeAdded(gesture);
            if (DefaultProfile == null)
                throw new InvalidOperationException("Set Default() before configuring gesture differences.");
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            var profile = _profiles.Create($"Gesture/{gesture}", DefaultProfile, configure);
            _gestureProfiles.Add(gesture, profile);
            return this;
        }

        internal ExpressionProfile Resolve(AacAv3.Av3Gesture gesture) =>
            _gestureProfiles.TryGetValue(gesture, out var profile) ? profile : DefaultProfile;

        private void EnsureGestureCanBeAdded(AacAv3.Av3Gesture gesture)
        {
            if (gesture == AacAv3.Av3Gesture.Neutral)
                throw new ArgumentException("Use Default() for the Neutral expression.", nameof(gesture));
            if (_gestureProfiles.ContainsKey(gesture))
                throw new InvalidOperationException($"A profile for gesture '{gesture}' is already defined.");
        }
    }

    /// <summary>ProfileをGestureごとのAnimator State Motionへ生成します。</summary>
    internal sealed class GestureProfileCompiler
    {
        private readonly FaceBuildContext _context;
        private readonly ClipChannelSplitter _splitter;
        private readonly LipSyncConfiguration _lipSync;
        private readonly AacFlLayer _layer;
        private readonly AacFlFloatParameter _referenceWeight;
        private readonly Dictionary<ExpressionProfile, Motion> _profileMotions =
            new Dictionary<ExpressionProfile, Motion>();
        private readonly Dictionary<ExpressionProfile, CompiledProfile> _compiledProfiles =
            new Dictionary<ExpressionProfile, CompiledProfile>();

        internal GestureProfileCompiler(
            FaceBuildContext context,
            ClipChannelSplitter splitter,
            LipSyncConfiguration lipSync)
        {
            _context = context;
            _splitter = splitter;
            _lipSync = lipSync;
            _layer = _context.Controller.NewLayer("Gesture Expressions");
            _referenceWeight = _layer.FloatParameter(_context.InternalName("Expression Reference Weight"));
            _layer.OverrideValue(_referenceWeight, 1f);
        }

        internal void Build(GestureProfileConfiguration configuration)
        {
            if (configuration.DefaultProfile == null)
                throw new InvalidOperationException("A default expression profile is required.");

            var layer = _layer;
            var av3 = layer.Av3();
            var gesture = av3.GestureRight;
            var states = new Dictionary<AacAv3.Av3Gesture, AacFlState>();
            var gestures = Enum.GetValues(typeof(AacAv3.Av3Gesture))
                .Cast<AacAv3.Av3Gesture>()
                .ToArray();
            var selectedProfiles = gestures.ToDictionary(value => value, configuration.Resolve);
            var compiledProfiles = selectedProfiles.Values
                .Distinct()
                .Select(PrepareProfile)
                .ToArray();
            var curveBindings = CollectOwnedBindings(compiledProfiles
                    .SelectMany(profile => profile.Clips)
                    .SelectMany(AnimationUtility.GetCurveBindings))
                .ToArray();
            var referenceValues = curveBindings.ToDictionary(
                CurveBindingKey.From,
                SampleReferenceValue);

            foreach (var value in gestures)
            {
                var selectedProfile = selectedProfiles[value];
                var selectedMotion = CompileProfile(
                    selectedProfile,
                    layer,
                    curveBindings,
                    referenceValues);
                var state = layer.NewState(value.ToString())
                    .WithAnimation(selectedMotion)
                    // 疎な子Clipが書かないチャンネルはDirect Treeの初期値に依存する。
                    // Write DefaultsをOffにすると前の姿勢が戻り、評価ごとにBlendShapeを増幅し得る。
                    .WithWriteDefaultsSetTo(true);
                foreach (var element in configuration.AnimatedTracking)
                    state.TrackingAnimates(element);
                states.Add(value, state);
            }

            layer.WithDefaultState(states[AacAv3.Av3Gesture.Neutral]);
            foreach (var source in states)
            foreach (var destination in states)
            {
                if (source.Key == destination.Key) continue;
                source.Value.TransitionsTo(destination.Value)
                    .WithTransitionDurationSeconds(configuration.TransitionDurationSeconds)
                    .WithSourceInterruption()
                    .WithNoTransitionToSelf()
                    .When(gesture.IsEqualTo(destination.Key));
            }
        }

        private CompiledProfile PrepareProfile(ExpressionProfile profile)
        {
            if (_compiledProfiles.TryGetValue(profile, out var existing)) return existing;

            var bindings = profile.Bindings
                .Select(binding => new CompiledBinding(
                    binding.Weight,
                    _splitter.Get(binding.SourceClip, binding.Channel),
                    binding.Channel,
                    binding.IsBaseClip))
                .Where(binding => binding.Clip != null)
                .ToArray();
            var bindings2D = profile.Bindings2D
                .Select(binding => new Compiled2DBinding(
                    binding.X,
                    binding.Y,
                    binding.Channel,
                    binding.Points.Select(point => new Compiled2DPoint(
                        point.X,
                        point.Y,
                        _splitter.Get(point.SourceClip, binding.Channel))).ToArray()))
                .ToArray();
            var compiled = new CompiledProfile(bindings, bindings2D);
            _compiledProfiles.Add(profile, compiled);
            return compiled;
        }

        private Motion CompileProfile(
            ExpressionProfile profile,
            AacFlLayer layer,
            IReadOnlyList<EditorCurveBinding> curveBindings,
            IReadOnlyDictionary<CurveBindingKey, float> referenceValues)
        {
            if (_profileMotions.TryGetValue(profile, out var existing)) return existing;

            var compiled = PrepareProfile(profile);
            var bindings = compiled.Bindings;
            var bindings2D = compiled.Bindings2D;
            Motion motion;
            if (bindings.Count == 0 && bindings2D.Count == 0)
            {
                motion = CreateReferencePoseClip(profile, curveBindings, referenceValues);
            }
            else
            {
                var referencePose = CreateReferencePoseClip(profile, curveBindings, referenceValues);

                var tree = _context.Aac.NewBlendTree().Direct();
                tree.BlendTree.name = $"Profile {profile.Name}";
                tree.WithAnimation(referencePose, _referenceWeight);
                foreach (var binding in bindings)
                {
                    // Bindが他のAACレイヤーで作られたParameterを使う場合もある。
                    // 生成Controllerだけで完結するよう、このレイヤーにも登録する。
                    layer.FloatParameter(binding.Weight.Name);
                    var bindingMotion = (Motion)CreateDeltaClip(
                        profile, binding.Clip, curveBindings, referenceValues);
                    if (!binding.IsBaseClip)
                        bindingMotion = ApplyLipSyncGate(
                            profile, bindingMotion, binding.Channel, curveBindings, layer);
                    tree.WithAnimation(bindingMotion, binding.Weight);
                }
                foreach (var binding in bindings2D)
                {
                    layer.FloatParameter(binding.X.Name);
                    layer.FloatParameter(binding.Y.Name);
                    var bindingMotion = Create2DBinding(
                        profile, binding, curveBindings, referenceValues);
                    bindingMotion = ApplyLipSyncGate(
                        profile, bindingMotion, binding.Channel, curveBindings, layer);
                    tree.WithAnimation(bindingMotion, _referenceWeight);
                }
                motion = tree.BlendTree;
            }

            _profileMotions.Add(profile, motion);
            return motion;
        }

        private AnimationClip CreateReferencePoseClip(
            ExpressionProfile profile,
            IEnumerable<EditorCurveBinding> bindings,
            IReadOnlyDictionary<CurveBindingKey, float> referenceValues)
        {
            var clip = _context.Aac.NewClip($"Reference Pose {profile.Name}").Clip;
            foreach (var binding in bindings)
                AnimationUtility.SetEditorCurve(
                    clip,
                    binding,
                    AnimationCurve.Constant(
                        0f,
                        1f / 60f,
                        referenceValues[CurveBindingKey.From(binding)]));
            return clip;
        }

        private static IEnumerable<EditorCurveBinding> CollectOwnedBindings(
            IEnumerable<EditorCurveBinding> sourceBindings)
        {
            // 全Gesture Profileと全子Motionに同じCurve集合を渡す。
            // 遷移先は前Gestureだけが書いたCurveを復元し、Rendererの無関係なPropertyは保持する。
            return sourceBindings
                .GroupBy(CurveBindingKey.From)
                .Select(group => group.First());
        }

        private AnimationClip CreateDeltaClip(
            ExpressionProfile profile,
            AnimationClip source,
            IReadOnlyList<EditorCurveBinding> allBindings,
            IReadOnlyDictionary<CurveBindingKey, float> referenceValues)
        {
            var delta = _context.Aac.NewClip($"Delta {profile.Name} - {source.name}").Clip;
            delta.frameRate = source.frameRate;
            delta.wrapMode = source.wrapMode;

            var sourceCurves = AnimationUtility.GetCurveBindings(source).ToDictionary(
                CurveBindingKey.From,
                binding => AnimationUtility.GetEditorCurve(source, binding));
            foreach (var binding in allBindings)
            {
                var key = CurveBindingKey.From(binding);
                var curve = sourceCurves.TryGetValue(key, out var sourceCurve)
                    ? SubtractReference(sourceCurve, referenceValues[key])
                    : AnimationCurve.Constant(0f, 1f / 60f, 0f);
                AnimationUtility.SetEditorCurve(delta, binding, curve);
            }

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(source))
                AnimationUtility.SetObjectReferenceCurve(
                    delta,
                    binding,
                    AnimationUtility.GetObjectReferenceCurve(source, binding));

            var settings = AnimationUtility.GetAnimationClipSettings(source);
            settings.hasAdditiveReferencePose = false;
            settings.additiveReferencePoseClip = null;
            settings.additiveReferencePoseTime = 0f;
            AnimationUtility.SetAnimationClipSettings(delta, settings);
            return delta;
        }

        private Motion Create2DBinding(
            ExpressionProfile profile,
            Compiled2DBinding binding,
            IReadOnlyList<EditorCurveBinding> allBindings,
            IReadOnlyDictionary<CurveBindingKey, float> referenceValues)
        {
            var tree = _context.Aac.NewBlendTreeAsRaw();
            tree.name = $"2D {profile.Name} - {binding.X.Name} x {binding.Y.Name}";
            tree.blendType = UnityEditor.Animations.BlendTreeType.FreeformDirectional2D;
            tree.blendParameter = binding.X.Name;
            tree.blendParameterY = binding.Y.Name;
            tree.useAutomaticThresholds = false;
            tree.children = binding.Points.Select(point => new UnityEditor.Animations.ChildMotion
            {
                motion = point.Clip != null
                    ? CreateDeltaClip(profile, point.Clip, allBindings, referenceValues)
                    : CreateEmptyDeltaClip(profile, allBindings),
                position = new Vector2(point.X, point.Y),
                timeScale = 1f
            }).ToArray();
            return tree;
        }

        private Motion ApplyLipSyncGate(
            ExpressionProfile profile,
            Motion trackedMotion,
            AnimationChannel channel,
            IReadOnlyList<EditorCurveBinding> allBindings,
            AacFlLayer layer)
        {
            var lipSyncWeight = _lipSync.LipSyncWeightFor(channel);
            if (lipSyncWeight == null) return trackedMotion;

            layer.FloatParameter(lipSyncWeight.Name);
            var tree = _context.Aac.NewBlendTreeAsRaw();
            tree.name = $"Lip Sync Gate {profile.Name} - {channel.Name}";
            tree.blendType = UnityEditor.Animations.BlendTreeType.Simple1D;
            tree.blendParameter = lipSyncWeight.Name;
            tree.useAutomaticThresholds = false;
            tree.children = new[]
            {
                new UnityEditor.Animations.ChildMotion
                {
                    motion = trackedMotion,
                    threshold = 0f,
                    timeScale = 1f
                },
                new UnityEditor.Animations.ChildMotion
                {
                    motion = CreateEmptyDeltaClip(profile, allBindings),
                    threshold = 1f,
                    timeScale = 1f
                }
            };
            return tree;
        }

        private AnimationClip CreateEmptyDeltaClip(
            ExpressionProfile profile,
            IEnumerable<EditorCurveBinding> allBindings)
        {
            var clip = _context.Aac.NewClip($"Empty Delta {profile.Name}").Clip;
            foreach (var binding in allBindings)
                AnimationUtility.SetEditorCurve(
                    clip, binding, AnimationCurve.Constant(0f, 1f / 60f, 0f));
            return clip;
        }

        private float SampleReferenceValue(EditorCurveBinding binding)
        {
            return AnimationUtility.GetFloatValue(_context.AnimatorRoot.gameObject, binding, out var value)
                ? value
                : 0f;
        }

        private static AnimationCurve SubtractReference(AnimationCurve source, float referenceValue)
        {
            var keys = source.keys;
            for (var index = 0; index < keys.Length; index++)
            {
                var key = keys[index];
                key.value -= referenceValue;
                keys[index] = key;
            }

            return new AnimationCurve(keys)
            {
                preWrapMode = source.preWrapMode,
                postWrapMode = source.postWrapMode
            };
        }

        private sealed class CompiledBinding
        {
            internal CompiledBinding(
                AacFlFloatParameter weight,
                AnimationClip clip,
                AnimationChannel channel,
                bool isBaseClip)
            {
                Weight = weight;
                Clip = clip;
                Channel = channel;
                IsBaseClip = isBaseClip;
            }

            internal AacFlFloatParameter Weight { get; }
            internal AnimationClip Clip { get; }
            internal AnimationChannel Channel { get; }
            internal bool IsBaseClip { get; }
        }

        private sealed class CompiledProfile
        {
            internal CompiledProfile(
                IReadOnlyList<CompiledBinding> bindings,
                IReadOnlyList<Compiled2DBinding> bindings2D)
            {
                Bindings = bindings;
                Bindings2D = bindings2D;
            }

            internal IReadOnlyList<CompiledBinding> Bindings { get; }
            internal IReadOnlyList<Compiled2DBinding> Bindings2D { get; }
            internal IEnumerable<AnimationClip> Clips => Bindings
                .Select(binding => binding.Clip)
                .Concat(Bindings2D.SelectMany(binding =>
                    binding.Points.Select(point => point.Clip)))
                .Where(clip => clip != null);
        }

        private sealed class Compiled2DBinding
        {
            internal Compiled2DBinding(
                AacFlFloatParameter x,
                AacFlFloatParameter y,
                AnimationChannel channel,
                IReadOnlyList<Compiled2DPoint> points)
            {
                X = x;
                Y = y;
                Channel = channel;
                Points = points;
            }

            internal AacFlFloatParameter X { get; }
            internal AacFlFloatParameter Y { get; }
            internal AnimationChannel Channel { get; }
            internal IReadOnlyList<Compiled2DPoint> Points { get; }
        }

        private sealed class Compiled2DPoint
        {
            internal Compiled2DPoint(float x, float y, AnimationClip clip)
            {
                X = x;
                Y = y;
                Clip = clip;
            }

            internal float X { get; }
            internal float Y { get; }
            internal AnimationClip Clip { get; }
        }
    }
}
