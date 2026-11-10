using System;
using System.Collections.Generic;
using System.Linq;
using AnimatorAsCode.V1;
using AnimatorAsCode.V1.VRC;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace FaceExpressionFramework.Editor
{
    /// <summary>
    /// 選択した追跡チャンネルを、Animatorで駆動するVisemeアニメーションへ切り替えます。
    /// 基準Clipは維持するため、Gestureごとの基準姿勢を保てます。
    /// </summary>
    public sealed class LipSyncConfiguration
    {
        private readonly FaceBuildContext _context;
        private readonly HashSet<AnimationChannel> _channels = new HashSet<AnimationChannel>();
        private AacFlFloatParameter _preferTracking;
        private AacFlFloatParameter _lipSyncWeight;
        private VisemeAnimationSet _visemes;
        private float _voiceThreshold = 0.01f;
        private float _bypassAbove = 0.25f;
        private float _returnBelow = 0.15f;
        private float _transitionDuration = 0.1f;
        private bool _built;

        internal LipSyncConfiguration(FaceBuildContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>AnimatorでLipSyncを行うときのVisemeセットを設定します。</summary>
        public LipSyncConfiguration Use(VisemeAnimationSet visemes)
        {
            EnsureNotBuilt();
            _visemes = visemes ?? throw new ArgumentNullException(nameof(visemes));
            return this;
        }

        /// <summary>発話中にLipSyncへ切り替える追跡チャンネルを選びます。Visemeカーブもこのチャンネルに限定します。</summary>
        public LipSyncConfiguration ReplaceTrackingWhileSpeaking(AnimationChannel channel)
        {
            EnsureNotBuilt();
            if (channel == null) throw new ArgumentNullException(nameof(channel));
            _channels.Add(channel);
            return this;
        }

        /// <summary>発話中にLipSyncへ切り替える追跡チャンネル群を選びます。Visemeカーブもこれらに限定します。</summary>
        public LipSyncConfiguration ReplaceTrackingWhileSpeaking(
            AnimationChannelSelection selection)
        {
            EnsureNotBuilt();
            if (selection == null) throw new ArgumentNullException(nameof(selection));
            foreach (var channel in selection.Channels) _channels.Add(channel);
            return this;
        }

        /// <summary>追跡抑制を始めるVoice値を設定します。</summary>
        public LipSyncConfiguration VoiceThreshold(float threshold)
        {
            EnsureNotBuilt();
            ValidateNormalized(threshold, nameof(threshold));
            _voiceThreshold = threshold;
            return this;
        }

        /// <summary>
        /// Signalが<paramref name="above"/>を超えたら追跡を優先し、
        /// <paramref name="returnBelow"/>を下回ったら再びLipSyncを許可します。
        /// </summary>
        private LipSyncConfiguration PreferTrackingWhen(
            ParameterSignal signal,
            float above,
            float returnBelow)
        {
            EnsureNotBuilt();
            if (signal == null) throw new ArgumentNullException(nameof(signal));
            ValidateNormalized(above, nameof(above));
            ValidateNormalized(returnBelow, nameof(returnBelow));
            if (returnBelow >= above)
                throw new ArgumentException("returnBelow must be less than above.");
            _preferTracking = signal.Parameter;
            _bypassAbove = above;
            _returnBelow = returnBelow;
            return this;
        }

        public LipSyncConfiguration PreferTrackingWhen(
            UnitSignal signal, float above, float returnBelow)
        {
            if (signal == null) throw new ArgumentNullException(nameof(signal));
            return PreferTrackingWhen(signal.Inner, above, returnBelow);
        }

        /// <summary>追跡とLipSyncの切り替え、およびViseme間の遷移時間を設定します。</summary>
        public LipSyncConfiguration TransitionDuration(float seconds)
        {
            EnsureNotBuilt();
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f)
                throw new ArgumentOutOfRangeException(nameof(seconds));
            _transitionDuration = seconds;
            return this;
        }

        internal AacFlFloatParameter LipSyncWeightFor(AnimationChannel channel) =>
            channel != null && _channels.Contains(channel) ? _lipSyncWeight : null;

        internal void BuildControl()
        {
            EnsureNotBuilt();
            _built = true;
            if (_channels.Count == 0) return;
            if (_visemes == null)
                throw new InvalidOperationException(
                    "Lip sync replacement requires Use().");
            if (_preferTracking == null)
                throw new InvalidOperationException(
                    "Lip sync replacement requires PreferTrackingWhen().");

            var layer = _context.Controller.NewLayer("Lip Sync Tracking Suppression")
                .WithAvatarMaskNoTransforms();
            var voice = layer.Av3().Voice;
            var preferTracking = layer.FloatParameter(_preferTracking.Name);
            _lipSyncWeight = layer.FloatParameter(
                _context.InternalName("LipSync/Weight"));
            layer.OverrideValue(_lipSyncWeight, 0f);

            var tracking = layer.NewState("Tracking")
                .WithAnimation(Constant(_lipSyncWeight, 0f, "Lip Sync Disabled"))
                .WithWriteDefaultsSetTo(false);
            var lipSync = layer.NewState("Lip Sync")
                .WithAnimation(Constant(_lipSyncWeight, 1f, "Lip Sync Enabled"))
                .WithWriteDefaultsSetTo(false);
            layer.WithDefaultState(tracking);

            tracking.TransitionsTo(lipSync)
                .WithTransitionDurationSeconds(_transitionDuration)
                .WithSourceInterruption()
                .When(voice.IsGreaterThan(_voiceThreshold))
                .And(preferTracking.IsLessThan(_returnBelow));
            lipSync.TransitionsTo(tracking)
                .WithTransitionDurationSeconds(_transitionDuration)
                .WithSourceInterruption()
                .When(voice.IsLessThan(_voiceThreshold));
            lipSync.TransitionsTo(tracking)
                .WithTransitionDurationSeconds(_transitionDuration)
                .WithSourceInterruption()
                .When(preferTracking.IsGreaterThan(_bypassAbove));
        }

        internal void BuildVisemes()
        {
            if (_channels.Count == 0) return;

            var layer = _context.Controller.NewLayer("Lip Sync Visemes");
            var visemeParameter = layer.Av3().Viseme;
            layer.FloatParameter(_lipSyncWeight.Name);

            var allBindings = _visemes.AllClips
                .SelectMany(SelectedBindings)
                .GroupBy(CurveBindingKey.From)
                .Select(group => group.First())
                .ToArray();
            if (allBindings.Length == 0)
                throw new InvalidOperationException(
                    "Selected lip-sync channels do not contain any curves from the Viseme clips.");
            var inactive = CompleteClip("Viseme Inactive", _visemes.InactiveClip, allBindings);
            var states = new Dictionary<AacAv3.Av3Viseme, AacFlState>();

            foreach (AacAv3.Av3Viseme viseme in Enum.GetValues(typeof(AacAv3.Av3Viseme)))
            {
                var active = CompleteClip(
                    $"Viseme {viseme}",
                    _visemes[viseme],
                    allBindings,
                    inactive);
                var blend = _context.Aac.NewBlendTreeAsRaw();
                blend.name = $"Viseme {viseme} Weight";
                blend.blendType = BlendTreeType.Simple1D;
                blend.blendParameter = _lipSyncWeight.Name;
                blend.useAutomaticThresholds = false;
                blend.children = new[]
                {
                    new ChildMotion { motion = inactive, threshold = 0f, timeScale = 1f },
                    new ChildMotion { motion = active, threshold = 1f, timeScale = 1f }
                };
                states.Add(viseme, layer.NewState(viseme.ToString())
                    .WithAnimation(blend)
                    .WithWriteDefaultsSetTo(true));
            }

            layer.WithDefaultState(states[AacAv3.Av3Viseme.sil]);
            foreach (var source in states)
            foreach (var destination in states)
            {
                if (source.Key == destination.Key) continue;
                source.Value.TransitionsTo(destination.Value)
                    .WithTransitionDurationSeconds(_transitionDuration)
                    .WithSourceInterruption()
                    .WithNoTransitionToSelf()
                    .When(visemeParameter.IsEqualTo(destination.Key));
            }
        }

        private AnimationClip CompleteClip(
            string label,
            AnimationClip source,
            IReadOnlyList<EditorCurveBinding> allBindings,
            AnimationClip fallback = null)
        {
            var completed = _context.Aac.NewClip(label + " Complete").Clip;
            var sourceCurves = AnimationUtility.GetCurveBindings(source).ToDictionary(
                CurveBindingKey.From,
                binding => AnimationUtility.GetEditorCurve(source, binding));
            var fallbackCurves = fallback == null
                ? new Dictionary<CurveBindingKey, AnimationCurve>()
                : AnimationUtility.GetCurveBindings(fallback).ToDictionary(
                    CurveBindingKey.From,
                    binding => AnimationUtility.GetEditorCurve(fallback, binding));
            var sourceObjects = AnimationUtility.GetObjectReferenceCurveBindings(source).ToDictionary(
                CurveBindingKey.From,
                binding => AnimationUtility.GetObjectReferenceCurve(source, binding));
            var fallbackObjects = fallback == null
                ? new Dictionary<CurveBindingKey, ObjectReferenceKeyframe[]>()
                : AnimationUtility.GetObjectReferenceCurveBindings(fallback).ToDictionary(
                    CurveBindingKey.From,
                    binding => AnimationUtility.GetObjectReferenceCurve(fallback, binding));
            foreach (var binding in allBindings)
            {
                var key = CurveBindingKey.From(binding);
                if (binding.isPPtrCurve)
                {
                    var frames = sourceObjects.TryGetValue(key, out var sourceFrames)
                        ? sourceFrames
                        : fallbackObjects.TryGetValue(key, out var fallbackFrames)
                            ? fallbackFrames
                            : new[] { new ObjectReferenceKeyframe { time = 0f, value = null } };
                    AnimationUtility.SetObjectReferenceCurve(completed, binding, frames);
                    continue;
                }
                var curve = sourceCurves.TryGetValue(key, out var existing)
                    ? existing
                    : fallbackCurves.TryGetValue(key, out var fallbackCurve)
                        ? fallbackCurve
                        : AnimationCurve.Constant(0f, 1f / 60f, 0f);
                AnimationUtility.SetEditorCurve(completed, binding, curve);
            }
            return completed;
        }

        private IEnumerable<EditorCurveBinding> SelectedBindings(AnimationClip clip)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip)
                .Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
            {
                foreach (var owner in _channels.Select(channel => channel.Owner).Distinct())
                {
                    var matches = owner.Definitions.Where(definition => definition.Matches(binding)).ToArray();
                    if (matches.Length > 1)
                        throw new InvalidOperationException(
                            $"Viseme curve '{binding.propertyName}' in '{clip.name}' matches multiple channels in '{owner.Name}'.");
                    var channel = matches.Length == 1 ? owner[matches[0].Name] : owner.Default;
                    if (_channels.Contains(channel))
                    {
                        yield return binding;
                        break;
                    }
                }
            }
        }

        private AnimationClip Constant(AacFlFloatParameter output, float value, string label)
        {
            var clip = _context.Aac.NewClip(label).Clip;
            clip.SetCurve(
                string.Empty,
                typeof(Animator),
                output.Name,
                new AnimationCurve(new Keyframe(0f, value)));
            return clip;
        }

        private void EnsureNotBuilt()
        {
            if (_built)
                throw new InvalidOperationException("Lip-sync suppression has already been built.");
        }

        private static void ValidateNormalized(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 1f)
                throw new ArgumentOutOfRangeException(parameterName, "Value must be in the 0..1 range.");
        }
    }
}
