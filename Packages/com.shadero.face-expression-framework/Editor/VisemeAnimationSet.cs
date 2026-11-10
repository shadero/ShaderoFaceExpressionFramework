using System;
using System.Collections.Generic;
using System.Linq;
using AnimatorAsCode.V1.VRC;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;

namespace FaceExpressionFramework.Editor
{
    /// <summary>
    /// 非アクティブ時の口の姿勢と、各VRChat Viseme値に対応するClipを定義します。
    /// Clipは手作業で作るか、Avatar Descriptorから取り込めます。
    /// </summary>
    public sealed class VisemeAnimationSet
    {
        private readonly IReadOnlyDictionary<AacAv3.Av3Viseme, AnimationClip> _clips;

        public VisemeAnimationSet(
            AnimationClip inactiveClip,
            IReadOnlyDictionary<AacAv3.Av3Viseme, AnimationClip> clips)
        {
            InactiveClip = inactiveClip != null
                ? inactiveClip
                : throw new ArgumentNullException(nameof(inactiveClip));
            if (clips == null) throw new ArgumentNullException(nameof(clips));

            var copy = new Dictionary<AacAv3.Av3Viseme, AnimationClip>();
            foreach (AacAv3.Av3Viseme viseme in Enum.GetValues(typeof(AacAv3.Av3Viseme)))
            {
                if (!clips.TryGetValue(viseme, out var clip) || clip == null)
                    throw new ArgumentException($"A clip is required for Viseme '{viseme}'.", nameof(clips));
                copy.Add(viseme, clip);
            }

            InactiveClip = inactiveClip;
            _clips = copy;
        }

        public AnimationClip InactiveClip { get; }
        public AnimationClip this[AacAv3.Av3Viseme viseme] => _clips[viseme];
        internal IEnumerable<AnimationClip> AllClips => _clips.Values.Prepend(InactiveClip);
    }

    /// <summary>
    /// Avatar DescriptorのVisemeBlendShape設定をClipへ取り込み、Descriptorを
    /// VisemeParameterOnlyへ切り替えます。元PrefabではなくNDMFビルド用Cloneで呼び出します。
    /// </summary>
    public static class AvatarDescriptorVisemeImporter
    {
        public static VisemeAnimationSet TakeControl(FaceBuildContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            var descriptor = context.AnimatorRoot.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null)
                throw new InvalidOperationException("The animator root has no VRCAvatarDescriptor.");
            return TakeControl(context, descriptor);
        }

        public static VisemeAnimationSet TakeControl(
            FaceBuildContext context,
            VRCAvatarDescriptor descriptor)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (descriptor.lipSync != VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape)
                throw new InvalidOperationException(
                    "Avatar Descriptor lip sync must be Viseme Blend Shape before it can be imported.");

            var renderer = descriptor.VisemeSkinnedMesh;
            if (renderer == null || renderer.sharedMesh == null)
                throw new InvalidOperationException("Avatar Descriptor has no valid Viseme Skinned Mesh.");

            var names = descriptor.VisemeBlendShapes;
            var visemes = Enum.GetValues(typeof(AacAv3.Av3Viseme))
                .Cast<AacAv3.Av3Viseme>()
                .ToArray();
            if (names == null || names.Length != visemes.Length)
                throw new InvalidOperationException(
                    $"Avatar Descriptor must define exactly {visemes.Length} Viseme blend shapes.");

            var meshNames = Enumerable.Range(0, renderer.sharedMesh.blendShapeCount)
                .Select(renderer.sharedMesh.GetBlendShapeName)
                .ToHashSet(StringComparer.Ordinal);
            for (var index = 0; index < names.Length; index++)
            {
                if (string.IsNullOrWhiteSpace(names[index]) || !meshNames.Contains(names[index]))
                    throw new InvalidOperationException(
                        $"Viseme '{visemes[index]}' references missing blend shape '{names[index]}'.");
            }

            var path = UnityEditor.AnimationUtility.CalculateTransformPath(
                renderer.transform,
                context.AnimatorRoot);
            var inactive = CreateClip(context, "Viseme Inactive", path, names, activeIndex: -1);
            var clips = new Dictionary<AacAv3.Av3Viseme, AnimationClip>();
            for (var index = 0; index < visemes.Length; index++)
                clips.Add(visemes[index], CreateClip(
                    context,
                    $"Viseme {visemes[index]}",
                    path,
                    names,
                    index));

            descriptor.lipSync = VRC_AvatarDescriptor.LipSyncStyle.VisemeParameterOnly;
            return new VisemeAnimationSet(inactive, clips);
        }

        private static AnimationClip CreateClip(
            FaceBuildContext context,
            string name,
            string rendererPath,
            IReadOnlyList<string> blendShapeNames,
            int activeIndex)
        {
            var clip = context.Aac.NewClip(name).Clip;
            for (var index = 0; index < blendShapeNames.Count; index++)
            {
                clip.SetCurve(
                    rendererPath,
                    typeof(SkinnedMeshRenderer),
                    "blendShape." + blendShapeNames[index],
                    AnimationCurve.Constant(0f, 1f / 60f, index == activeIndex ? 100f : 0f));
            }
            return clip;
        }
    }
}
