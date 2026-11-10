using System;
using System.Collections.Generic;
using System.Linq;
using AnimatorAsCode.V1;
using UnityEngine;

namespace FaceExpressionFramework.Editor
{
    /// <summary>独立制御するClipチャンネルに最終係数をBindします。</summary>
    internal sealed class ExpressionBinding
    {
        internal ExpressionBinding(
            AacFlFloatParameter weight,
            AnimationClip sourceClip,
            AnimationChannel channel,
            bool isBaseClip = false,
            ClipBind definition = null)
        {
            Weight = weight;
            SourceClip = sourceClip;
            Channel = channel;
            IsBaseClip = isBaseClip;
            Definition = definition;
        }

        public AacFlFloatParameter Weight { get; }
        public AnimationClip SourceClip { get; }
        public AnimationChannel Channel { get; }
        internal bool IsBaseClip { get; }
        internal ClipBind Definition { get; }
    }

    public sealed class ExpressionProfile
    {
        internal ExpressionProfile(
            string name,
            IReadOnlyList<ExpressionBinding> bindings,
            IReadOnlyList<Expression2DBinding> bindings2D)
        {
            Name = name;
            Bindings = bindings;
            Bindings2D = bindings2D;
        }

        public string Name { get; }
        internal IReadOnlyList<ExpressionBinding> Bindings { get; }
        internal IReadOnlyList<Expression2DBinding> Bindings2D { get; }
    }

    public sealed class ProfileDraft
    {
        private readonly string _name;
        private readonly Func<AacFlFloatParameter> _constantWeight;
        private readonly AnimationChannelRegistry _channels;
        private readonly List<ExpressionBinding> _bindings = new List<ExpressionBinding>();
        private readonly List<Expression2DBinding> _bindings2D = new List<Expression2DBinding>();
        private readonly HashSet<AnimationChannel> _excludedChannels = new HashSet<AnimationChannel>();

        internal ProfileDraft(
            string name,
            Func<AacFlFloatParameter> constantWeight,
            AnimationChannelRegistry channels)
        {
            _name = name;
            _constantWeight = constantWeight;
            _channels = channels;
        }

        /// <summary>再利用する基準ProfileからすべてのBindを継承します。</summary>
        internal ProfileDraft Include(ExpressionProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            _bindings.AddRange(profile.Bindings);
            _bindings2D.AddRange(profile.Bindings2D);
            return this;
        }

        public ProfileDraft Add(ClipBind binding)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            var owners = _bindings.Select(existing => existing.Channel.Owner)
                .Concat(_bindings2D.Select(existing => existing.Channel.Owner))
                .Distinct()
                .ToArray();
            if (binding.Channel == null && owners.Length == 0)
                owners = _channels.AllSets.ToArray();
            if (binding.Channel == null && owners.Length != 1)
                throw new InvalidOperationException(
                    "ClipBind without a channel requires exactly one channel set in the profile or framework.");
            var channels = binding.Channel != null
                ? new[] { binding.Channel }
                : owners[0].AllChannels;
            foreach (var channel in channels)
                _bindings.Add(new ExpressionBinding(
                    binding.Signal.ResolveParameter(), binding.Clip, channel,
                    definition: binding));
            return this;
        }

        public ProfileDraft Add(SurfaceBind binding)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            _bindings2D.Add(binding.Binding);
            return this;
        }

        /// <summary>指定した継承BindのClipだけを、チャンネルを維持して置き換えます。</summary>
        public ProfileDraft ReplaceClip(ClipBind binding, AnimationClip replacement)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            if (replacement == null) throw new ArgumentNullException(nameof(replacement));
            var replaced = 0;
            for (var index = 0; index < _bindings.Count; index++)
            {
                var existing = _bindings[index];
                if (!ReferenceEquals(existing.Definition, binding)) continue;
                _bindings[index] = new ExpressionBinding(
                    existing.Weight, replacement, existing.Channel,
                    existing.IsBaseClip, existing.Definition);
                replaced++;
            }
            if (replaced == 0)
                throw new InvalidOperationException("The inherited profile does not contain this binding.");
            return this;
        }

        public ProfileDraft Without(ClipBind binding)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            if (_bindings.RemoveAll(existing => ReferenceEquals(existing.Definition, binding)) == 0)
                throw new InvalidOperationException("The inherited profile does not contain this binding.");
            return this;
        }

        public ProfileDraft Without(SurfaceBind binding)
        {
            if (binding == null) throw new ArgumentNullException(nameof(binding));
            if (!_bindings2D.Remove(binding.Binding))
                throw new InvalidOperationException("The inherited profile does not contain this surface.");
            return this;
        }

        public ProfileDraft WithoutBindingsUsing(UnitSignal signal)
        {
            if (signal == null) throw new ArgumentNullException(nameof(signal));
            return Without(signal.Inner);
        }

        /// <summary>指定Signalで駆動する継承Bindをすべて除外します。</summary>
        private ProfileDraft Without(ParameterSignal signal)
        {
            var weight = signal.Parameter;
            var removed = _bindings.RemoveAll(binding =>
                string.Equals(binding.Weight.Name, weight.Name, StringComparison.Ordinal));
            removed += _bindings2D.RemoveAll(binding => Uses(binding, weight));
            if (removed == 0)
                throw new InvalidOperationException("No inherited binding uses signal '" + weight.Name + "'.");
            return this;
        }

        /// <summary>
        /// 指定チャンネルのパラメータで駆動する1D・2D Bindを除外します。
        /// Gestureの基準Clipは維持します。
        /// </summary>
        public ProfileDraft Without(AnimationChannel channel)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));
            _excludedChannels.Add(channel);
            return this;
        }

        /// <summary>
        /// このProfileの1D Bindまたは2D Surfaceで使用する全チャンネルセットの基準Clipを置き換えます。
        /// それ以外のBindとSurfaceは維持します。
        /// </summary>
        public ProfileDraft BaseClip(AnimationClip sourceClip)
        {
            var channels = _bindings.Select(binding => binding.Channel.Owner)
                .Concat(_bindings2D.Select(binding => binding.Channel.Owner))
                .Distinct()
                .SelectMany(owner => owner.AllChannels)
                .ToArray();
            if (channels.Length == 0)
                throw new InvalidOperationException(
                    "BaseClip without an explicit channel requires an inherited or existing binding or surface.");
            return BaseClip(sourceClip, new AnimationChannelSelection(channels));
        }

        /// <summary>選択したチャンネルだけの基準Clipを置き換えます。</summary>
        private ProfileDraft BaseClip(
            AnimationClip sourceClip,
            AnimationChannelSelection selection)
        {
            if (sourceClip == null) throw new ArgumentNullException(nameof(sourceClip));
            if (selection == null) throw new ArgumentNullException(nameof(selection));

            var selected = new HashSet<AnimationChannel>(selection.Channels);
            _bindings.RemoveAll(binding => binding.IsBaseClip && selected.Contains(binding.Channel));

            var constantWeight = _constantWeight();
            foreach (var channel in selection.Channels)
                _bindings.Add(new ExpressionBinding(constantWeight, sourceClip, channel, isBaseClip: true));
            return this;
        }

        /// <summary>1つのチャンネルの基準Clipを設定または置き換えます。</summary>
        public ProfileDraft BaseClip(AnimationClip sourceClip, AnimationChannel channel)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));
            return BaseClip(sourceClip, new AnimationChannelSelection(new[] { channel }));
        }

        internal ExpressionProfile Build()
        {
            var bindings2D = _bindings2D
                .Where(binding => !_excludedChannels.Contains(binding.Channel))
                .ToArray();
            return new ExpressionProfile(
                _name,
                _bindings.Where(binding =>
                    binding.IsBaseClip || !_excludedChannels.Contains(binding.Channel)).ToArray(),
                bindings2D);
        }

        private static bool Uses(Expression2DBinding binding, AacFlFloatParameter parameter) =>
            string.Equals(binding.X.Name, parameter.Name, StringComparison.Ordinal) ||
            string.Equals(binding.Y.Name, parameter.Name, StringComparison.Ordinal);

    }

    /// <summary>名前付きProfileを作成します。共通Bindは通常のC#ヘルパーで再利用できます。</summary>
    public sealed class ExpressionProfileRegistry
    {
        private readonly Func<AacFlFloatParameter> _constantWeight;
        private readonly AnimationChannelRegistry _channels;
        private readonly Dictionary<string, ExpressionProfile> _profiles =
            new Dictionary<string, ExpressionProfile>(StringComparer.Ordinal);

        internal ExpressionProfileRegistry(
            Func<AacFlFloatParameter> constantWeight,
            AnimationChannelRegistry channels)
        {
            _constantWeight = constantWeight ?? throw new ArgumentNullException(nameof(constantWeight));
            _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        }

        public ExpressionProfile Create(string name, Action<ProfileDraft> configure)
        {
            return Create(name, null, configure);
        }

        internal ExpressionProfile Create(
            string name,
            ExpressionProfile baseProfile,
            Action<ProfileDraft> configure)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A profile name is required.", nameof(name));
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            if (_profiles.ContainsKey(name))
                throw new InvalidOperationException($"Expression profile '{name}' is already defined.");

            var builder = new ProfileDraft(name, _constantWeight, _channels);
            if (baseProfile != null) builder.Include(baseProfile);
            configure(builder);
            var profile = builder.Build();
            _profiles.Add(name, profile);
            return profile;
        }
    }
}
