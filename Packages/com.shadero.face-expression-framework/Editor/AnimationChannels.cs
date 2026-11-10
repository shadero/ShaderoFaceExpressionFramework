using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AnimatorAsCode.V1;
using UnityEditor;
using UnityEngine;

namespace FaceExpressionFramework.Editor
{
    public sealed class AnimationChannel
    {
        internal AnimationChannel(AnimationChannelSet owner, string name)
        {
            Owner = owner;
            Name = name;
        }

        internal AnimationChannelSet Owner { get; }
        public string Name { get; }
        public override string ToString() => $"{Owner.Name}/{Name}";
    }

    /// <summary>アバター側で定義する再利用可能なAnimation Curve分類です。</summary>
    public sealed class AnimationChannelSet
    {
        internal const string DefaultChannelName = "Default";
        private readonly Dictionary<string, ChannelDefinition> _definitions;
        private readonly Dictionary<string, AnimationChannel> _channels;
        private readonly IReadOnlyList<AnimationChannel> _allChannels;

        internal AnimationChannelSet(string name, IReadOnlyList<ChannelDefinition> definitions)
        {
            Name = name;
            _definitions = definitions.ToDictionary(item => item.Name, StringComparer.Ordinal);
            _channels = definitions.ToDictionary(
                item => item.Name,
                item => new AnimationChannel(this, item.Name),
                StringComparer.Ordinal);
            _channels.Add(DefaultChannelName, new AnimationChannel(this, DefaultChannelName));
            _allChannels = definitions
                .Select(item => _channels[item.Name])
                .Concat(new[] { _channels[DefaultChannelName] })
                .ToArray();
        }

        public string Name { get; }
        public AnimationChannel Default => _channels[DefaultChannelName];
        internal IEnumerable<ChannelDefinition> Definitions => _definitions.Values;
        internal IReadOnlyList<AnimationChannel> AllChannels => _allChannels;

        public AnimationChannel this[string channelName]
        {
            get
            {
                if (!_channels.TryGetValue(channelName, out var channel))
                    throw new KeyNotFoundException($"Channel '{channelName}' is not defined in '{Name}'.");
                return channel;
            }
        }
    }

    /// <summary>1つのチャンネルセットから明示的に選ぶチャンネル群です。</summary>
    public sealed class AnimationChannelSelection
    {
        internal AnimationChannelSelection(IReadOnlyList<AnimationChannel> channels)
        {
            Channels = channels ?? throw new ArgumentNullException(nameof(channels));
            if (channels.Count == 0)
                throw new ArgumentException("A channel selection cannot be empty.", nameof(channels));
        }

        public IReadOnlyList<AnimationChannel> Channels { get; }
    }

    /// <summary>enumをキーとする型安全なチャンネルセットです。</summary>
    public sealed class AnimationChannelSet<TChannel> where TChannel : struct, Enum
    {
        private readonly AnimationChannelSet _set;
        private readonly TChannel[] _defined;

        internal AnimationChannelSet(AnimationChannelSet set, TChannel[] defined)
        {
            _set = set;
            _defined = defined;
        }

        public string Name => _set.Name;
        public AnimationChannel Default => _set.Default;
        public AnimationChannel this[TChannel channel] => _set[channel.ToString()];

        /// <summary>全enumチャンネルと未分類のDefaultチャンネルを選びます。</summary>
        public AnimationChannelSelection All => Select(_defined, includeDefault: true);

        /// <summary>指定したenumチャンネルだけを選びます。</summary>
        public AnimationChannelSelection Only(params TChannel[] channels)
        {
            if (channels == null) throw new ArgumentNullException(nameof(channels));
            return Select(Distinct(channels), includeDefault: false);
        }

        /// <summary>指定したenumチャンネル以外を、Defaultを含めて選びます。</summary>
        public AnimationChannelSelection Except(params TChannel[] channels)
        {
            if (channels == null) throw new ArgumentNullException(nameof(channels));
            var excluded = new HashSet<TChannel>(channels);
            return Select(_defined.Where(channel => !excluded.Contains(channel)), includeDefault: true);
        }

        private AnimationChannelSelection Select(
            IEnumerable<TChannel> channels,
            bool includeDefault)
        {
            var selected = channels.Select(channel => this[channel]).ToList();
            if (includeDefault) selected.Add(Default);
            return new AnimationChannelSelection(selected);
        }

        private static IEnumerable<TChannel> Distinct(IEnumerable<TChannel> channels) =>
            channels.Distinct();
    }

    /// <summary>名前付きチャンネルセットを定義します。顔部位の名前は固定しません。</summary>
    public sealed class AnimationChannelRegistry
    {
        private readonly Dictionary<string, AnimationChannelSet> _sets =
            new Dictionary<string, AnimationChannelSet>(StringComparer.Ordinal);

        internal IReadOnlyCollection<AnimationChannelSet> AllSets => _sets.Values;

        public AnimationChannelSet Define(string name, Action<AnimationChannelSetBuilder> configure)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A channel set name is required.", nameof(name));
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            if (_sets.ContainsKey(name))
                throw new InvalidOperationException($"Channel set '{name}' is already defined.");

            var builder = new AnimationChannelSetBuilder(name);
            configure(builder);
            var set = builder.Build();
            _sets.Add(name, set);
            return set;
        }

        public AnimationChannelSet<TChannel> Define<TChannel>(
            string name,
            Action<AnimationChannelSetBuilder<TChannel>> configure)
            where TChannel : struct, Enum
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A channel set name is required.", nameof(name));
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            if (_sets.ContainsKey(name))
                throw new InvalidOperationException($"Channel set '{name}' is already defined.");

            var builder = new AnimationChannelSetBuilder(name);
            var typedBuilder = new AnimationChannelSetBuilder<TChannel>(builder);
            configure(typedBuilder);
            var defined = typedBuilder.ValidateAndGetDefined();
            var set = builder.Build();
            _sets.Add(name, set);
            return new AnimationChannelSet<TChannel>(set, defined);
        }
    }

    /// <summary>enumの全メンバーをアバター固有のAnimationチャンネルとして定義します。</summary>
    public sealed class AnimationChannelSetBuilder<TChannel> where TChannel : struct, Enum
    {
        private readonly AnimationChannelSetBuilder _builder;
        private readonly HashSet<TChannel> _defined = new HashSet<TChannel>();

        internal AnimationChannelSetBuilder(AnimationChannelSetBuilder builder) =>
            _builder = builder;

        public AnimationChannelSetBuilder<TChannel> Channel(
            TChannel channel,
            Action<AnimationChannelDefinitionBuilder> configure)
        {
            if (!_defined.Add(channel))
                throw new InvalidOperationException("Channel '" + channel + "' is already defined.");
            _builder.Channel(channel.ToString(), configure);
            return this;
        }

        internal TChannel[] ValidateAndGetDefined()
        {
            var expected = ((TChannel[])Enum.GetValues(typeof(TChannel)));
            var missing = expected.Where(channel => !_defined.Contains(channel)).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException(
                    "Every channel enum member must be defined. Missing: " + string.Join(", ", missing));
            return expected;
        }
    }

    public sealed class AnimationChannelSetBuilder
    {
        private readonly string _name;
        private readonly List<ChannelDefinition> _definitions = new List<ChannelDefinition>();
        private readonly HashSet<string> _names = new HashSet<string>(StringComparer.Ordinal);

        internal AnimationChannelSetBuilder(string name) => _name = name;

        public AnimationChannelSetBuilder Channel(string name, Action<AnimationChannelDefinitionBuilder> configure)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("A channel name is required.", nameof(name));
            if (name == AnimationChannelSet.DefaultChannelName)
                throw new ArgumentException("'Default' is reserved for unclassified curves.", nameof(name));
            if (!_names.Add(name))
                throw new InvalidOperationException($"Channel '{name}' is already defined in '{_name}'.");
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            var builder = new AnimationChannelDefinitionBuilder(name);
            configure(builder);
            _definitions.Add(builder.Build());
            return this;
        }

        internal AnimationChannelSet Build()
        {
            if (_definitions.Count == 0)
                throw new InvalidOperationException($"Channel set '{_name}' must contain at least one channel.");
            return new AnimationChannelSet(_name, _definitions);
        }
    }

    public sealed class AnimationChannelDefinitionBuilder
    {
        private readonly string _name;
        private readonly List<Predicate<EditorCurveBinding>> _selectors =
            new List<Predicate<EditorCurveBinding>>();

        internal AnimationChannelDefinitionBuilder(string name) => _name = name;

        public AnimationChannelDefinitionBuilder Include(EditorCurveBinding binding)
        {
            var expected = CurveBindingKey.From(binding);
            _selectors.Add(candidate => CurveBindingKey.From(candidate).Equals(expected));
            return this;
        }

        public AnimationChannelDefinitionBuilder Include(Predicate<EditorCurveBinding> selector)
        {
            _selectors.Add(selector ?? throw new ArgumentNullException(nameof(selector)));
            return this;
        }

        internal ChannelDefinition Build()
        {
            if (_selectors.Count == 0)
                throw new InvalidOperationException($"Channel '{_name}' must contain at least one curve selector.");
            return new ChannelDefinition(_name, _selectors);
        }
    }

    internal sealed class ChannelDefinition
    {
        private readonly IReadOnlyList<Predicate<EditorCurveBinding>> _selectors;

        internal ChannelDefinition(string name, IReadOnlyList<Predicate<EditorCurveBinding>> selectors)
        {
            Name = name;
            _selectors = selectors;
        }

        internal string Name { get; }
        internal bool Matches(EditorCurveBinding binding) => _selectors.Any(selector => selector(binding));
    }

    internal readonly struct CurveBindingKey : IEquatable<CurveBindingKey>
    {
        private CurveBindingKey(string path, Type type, string propertyName, bool isObjectReference)
        {
            Path = path ?? string.Empty;
            Type = type;
            PropertyName = propertyName ?? string.Empty;
            IsObjectReference = isObjectReference;
        }

        private string Path { get; }
        private Type Type { get; }
        private string PropertyName { get; }
        private bool IsObjectReference { get; }

        internal static CurveBindingKey From(EditorCurveBinding binding) => new CurveBindingKey(
            binding.path, binding.type, binding.propertyName, binding.isPPtrCurve);

        public bool Equals(CurveBindingKey other) =>
            Path == other.Path && Type == other.Type && PropertyName == other.PropertyName &&
            IsObjectReference == other.IsObjectReference;

        public override bool Equals(object obj) => obj is CurveBindingKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = Path.GetHashCode();
                hashCode = (hashCode * 397) ^ (Type != null ? Type.GetHashCode() : 0);
                hashCode = (hashCode * 397) ^ PropertyName.GetHashCode();
                hashCode = (hashCode * 397) ^ IsObjectReference.GetHashCode();
                return hashCode;
            }
        }
    }

    /// <summary>元Clipをチャンネルごとに分割し、生成結果をキャッシュします。</summary>
    internal sealed class ClipChannelSplitter
    {
        private readonly AacFlBase _aac;
        private readonly Dictionary<SplitCacheKey, IReadOnlyDictionary<string, AnimationClip>> _cache =
            new Dictionary<SplitCacheKey, IReadOnlyDictionary<string, AnimationClip>>();

        internal ClipChannelSplitter(AacFlBase aac) => _aac = aac;

        internal AnimationClip Get(AnimationClip source, AnimationChannel channel)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (channel == null) throw new ArgumentNullException(nameof(channel));

            var key = new SplitCacheKey(source.GetInstanceID(), channel.Owner);
            if (!_cache.TryGetValue(key, out var split))
            {
                split = Split(source, channel.Owner);
                _cache.Add(key, split);
            }
            return split.TryGetValue(channel.Name, out var clip) ? clip : null;
        }

        private IReadOnlyDictionary<string, AnimationClip> Split(AnimationClip source, AnimationChannelSet channelSet)
        {
            var floatBindings = AnimationUtility.GetCurveBindings(source);
            var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(source);
            var assignments = new Dictionary<string, List<EditorCurveBinding>>(StringComparer.Ordinal)
            {
                [AnimationChannelSet.DefaultChannelName] = new List<EditorCurveBinding>()
            };
            foreach (var definition in channelSet.Definitions)
                assignments.Add(definition.Name, new List<EditorCurveBinding>());

            foreach (var binding in floatBindings.Concat(objectBindings))
            {
                var matches = channelSet.Definitions
                    .Where(definition => definition.Matches(binding))
                    .Select(definition => definition.Name)
                    .ToArray();
                if (matches.Length > 1)
                {
                    throw new InvalidOperationException(
                        $"Curve '{Format(binding)}' in clip '{source.name}' matches multiple channels: " +
                        string.Join(", ", matches));
                }
                assignments[matches.Length == 1 ? matches[0] : AnimationChannelSet.DefaultChannelName].Add(binding);
            }

            var result = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
            foreach (var pair in assignments.Where(pair => pair.Value.Count > 0))
            {
                var generated = _aac.CopyClip(source).Clip;
                generated.name = $"{source.name} [{channelSet.Name}/{pair.Key}]";
                ClearCurves(generated);
                foreach (var binding in pair.Value)
                {
                    if (binding.isPPtrCurve)
                    {
                        AnimationUtility.SetObjectReferenceCurve(
                            generated, binding, AnimationUtility.GetObjectReferenceCurve(source, binding));
                    }
                    else
                    {
                        AnimationUtility.SetEditorCurve(
                            generated, binding, AnimationUtility.GetEditorCurve(source, binding));
                    }
                }
                if (pair.Key != AnimationChannelSet.DefaultChannelName)
                    AnimationUtility.SetAnimationEvents(generated, Array.Empty<AnimationEvent>());
                result.Add(pair.Key, generated);
            }
            return result;
        }

        private static void ClearCurves(AnimationClip clip)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                AnimationUtility.SetEditorCurve(clip, binding, null);
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
        }

        private static string Format(EditorCurveBinding binding) =>
            $"{binding.path} | {binding.type?.Name} | {binding.propertyName}";

        private readonly struct SplitCacheKey : IEquatable<SplitCacheKey>
        {
            private readonly int _sourceId;
            private readonly AnimationChannelSet _channelSet;

            internal SplitCacheKey(int sourceId, AnimationChannelSet channelSet)
            {
                _sourceId = sourceId;
                _channelSet = channelSet;
            }

            public bool Equals(SplitCacheKey other) =>
                _sourceId == other._sourceId && ReferenceEquals(_channelSet, other._channelSet);
            public override bool Equals(object obj) => obj is SplitCacheKey other && Equals(other);
            public override int GetHashCode() => (_sourceId * 397) ^ RuntimeHelpers.GetHashCode(_channelSet);
        }
    }
}
