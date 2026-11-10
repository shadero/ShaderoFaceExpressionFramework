using System;
using System.Collections.Generic;
using System.Linq;
using AnimatorAsCode.V1;
using AnimatorAsCode.V1.VRC;
using UnityEditor.Animations;
using UnityEngine;

namespace FaceExpressionFramework.Editor
{
    /// <summary>
    /// Parameter Smoother 0.1.1と同じ漸化式で、Signal計算より前に生入力をSmoothします。
    /// 登録した全入力は1つのAnimatorレイヤーを共有します。
    /// </summary>
    internal sealed class SmoothingBuilder
    {
        private const float ParameterRange = 1024f;
        private readonly FaceBuildContext _context;
        private readonly string _requestedLayerName;
        private readonly string _outputPrefix;
        private readonly List<SmoothDefinition> _definitions = new List<SmoothDefinition>();
        private readonly HashSet<string> _outputNames = new HashSet<string>(StringComparer.Ordinal);
        private AacFlLayer _layer;
        private string _layerName;
        private bool _built;

        internal SmoothingBuilder(
            FaceBuildContext context,
            string layerName = "Parameter Smooth",
            string outputPrefix = "Smooth")
        {
            _context = context;
            _requestedLayerName = layerName;
            _outputPrefix = outputPrefix;
        }

        internal AacFlFloatParameter Add(
            AacFlFloatParameter source,
            float localSmoothness,
            float remoteSmoothness,
            string outputName = null)
        {
            if (_built) throw new InvalidOperationException("Smoothing has already been built.");
            if (source == null) throw new ArgumentNullException(nameof(source));
            ValidateSmoothness(localSmoothness, nameof(localSmoothness));
            ValidateSmoothness(remoteSmoothness, nameof(remoteSmoothness));

            outputName = string.IsNullOrWhiteSpace(outputName)
                ? _context.InternalName(_outputPrefix + "/" + source.Name)
                : outputName;
            if (outputName == source.Name)
                throw new ArgumentException("Smooth source and output parameters must have different names.");
            if (!_outputNames.Add(outputName))
                throw new InvalidOperationException($"Smooth output '{outputName}' is already registered.");

            EnsureLayer();
            _layer.FloatParameter(source.Name);
            var output = _layer.FloatParameter(outputName);
            var sourceDefault = _context.Controller.AnimatorController.parameters
                .First(parameter => parameter.name == source.Name).defaultFloat;
            _layer.OverrideValue(output, sourceDefault);
            var local = _layer.FloatParameter(_context.InternalName($"Smoothness/Local/{outputName}"));
            var remote = _layer.FloatParameter(_context.InternalName($"Smoothness/Remote/{outputName}"));
            _layer.OverrideValue(local, localSmoothness);
            _layer.OverrideValue(remote, remoteSmoothness);
            _definitions.Add(new SmoothDefinition(source, output, local, remote));
            return output;
        }

        internal void Build(string beforeLayerName)
        {
            if (_built) throw new InvalidOperationException("Smoothing has already been built.");
            _built = true;
            if (_definitions.Count == 0) return;
            MoveLayerBefore(beforeLayerName);

            var one = _layer.FloatParameter(_context.InternalName("One"));
            _layer.OverrideValue(one, 1f);
            var remote = _layer.NewState("Remote")
                .WithAnimation(CreateRoot(one, false))
                .WithWriteDefaultsSetTo(true);
            var local = _layer.NewState("Local")
                .WithAnimation(CreateRoot(one, true))
                .WithWriteDefaultsSetTo(true);
            _layer.WithDefaultState(remote);

            var isLocal = _layer.Av3().IsLocal;
            remote.TransitionsTo(local).WithTransitionDurationSeconds(0f).When(isLocal.IsTrue());
            local.TransitionsTo(remote).WithTransitionDurationSeconds(0f).When(isLocal.IsFalse());
        }

        private void EnsureLayer()
        {
            if (_layer == null)
            {
                _layer = _context.Controller.NewLayer(_requestedLayerName);
                _layerName = _context.Controller.AnimatorController.layers.Last().name;
            }
        }

        private void MoveLayerBefore(string beforeLayerName)
        {
            var controller = _context.Controller.AnimatorController;
            var layers = controller.layers.ToList();
            var index = layers.FindIndex(layer => layer.name == _layerName);
            var target = layers.FindIndex(layer => layer.name == beforeLayerName);
            if (index < 0 || target < 0 || index < target) return;
            var smoothingLayer = layers[index];
            layers.RemoveAt(index);
            layers.Insert(target, smoothingLayer);
            controller.layers = layers.ToArray();
        }

        private BlendTree CreateRoot(AacFlFloatParameter one, bool local)
        {
            var root = NewTree(local ? "Smooth Local" : "Smooth Remote");
            root.blendType = BlendTreeType.Direct;
            root.blendParameter = one.Name;
            root.children = _definitions.Select(definition => new ChildMotion
            {
                motion = CreateSmoothTree(definition, local),
                directBlendParameter = one.Name,
                timeScale = 1f
            }).ToArray();
            return root;
        }

        private BlendTree CreateSmoothTree(SmoothDefinition definition, bool local)
        {
            var minimum = CreateConstantClip(definition.Output.Name, -ParameterRange, "Minimum");
            var maximum = CreateConstantClip(definition.Output.Name, ParameterRange, "Maximum");
            var oldValue = CreateTransferTree(definition.Output.Name, minimum, maximum, "Previous");
            var newValue = CreateTransferTree(definition.Source.Name, minimum, maximum, "Source");

            var tree = NewTree($"Smooth {definition.Output.Name}");
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = (local ? definition.LocalSmoothness : definition.RemoteSmoothness).Name;
            tree.useAutomaticThresholds = false;
            tree.minThreshold = 0f;
            tree.maxThreshold = 1f;
            tree.children = new[]
            {
                new ChildMotion { motion = newValue, threshold = 0f, timeScale = 1f },
                new ChildMotion { motion = oldValue, threshold = 1f, timeScale = 1f }
            };
            return tree;
        }

        private BlendTree CreateTransferTree(string sourceParameter, Motion minimum, Motion maximum, string label)
        {
            var tree = NewTree($"{label} {sourceParameter}");
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = sourceParameter;
            tree.useAutomaticThresholds = false;
            tree.minThreshold = -ParameterRange;
            tree.maxThreshold = ParameterRange;
            tree.children = new[]
            {
                new ChildMotion { motion = minimum, threshold = -ParameterRange, timeScale = 1f },
                new ChildMotion { motion = maximum, threshold = ParameterRange, timeScale = 1f }
            };
            return tree;
        }

        private AnimationClip CreateConstantClip(string outputParameter, float value, string label)
        {
            var clip = _context.Aac.NewClip($"{label} {outputParameter}").Clip;
            clip.SetCurve(string.Empty, typeof(Animator), outputParameter,
                new AnimationCurve(new Keyframe(0f, value)));
            return clip;
        }

        private BlendTree NewTree(string name)
        {
            var tree = _context.Aac.NewBlendTreeAsRaw();
            tree.name = name;
            return tree;
        }

        private static void ValidateSmoothness(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 1f)
                throw new ArgumentOutOfRangeException(parameterName, "Smoothness must be in the 0..1 range.");
        }

        private sealed class SmoothDefinition
        {
            internal SmoothDefinition(
                AacFlFloatParameter source,
                AacFlFloatParameter output,
                AacFlFloatParameter localSmoothness,
                AacFlFloatParameter remoteSmoothness)
            {
                Source = source;
                Output = output;
                LocalSmoothness = localSmoothness;
                RemoteSmoothness = remoteSmoothness;
            }

            internal AacFlFloatParameter Source { get; }
            internal AacFlFloatParameter Output { get; }
            internal AacFlFloatParameter LocalSmoothness { get; }
            internal AacFlFloatParameter RemoteSmoothness { get; }
        }
    }
}
