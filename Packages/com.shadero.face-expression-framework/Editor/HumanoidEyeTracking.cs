using System;
using AnimatorAsCode.V1;
using AnimatorAsCode.V1.VRC;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace FaceExpressionFramework.Editor
{
    /// <summary>
    /// 必要なアバターだけが使うHumanoid Eyes連携です。
    /// Eye Look設定からMuscle姿勢を求め、4方向の正規化係数をBindします。
    /// </summary>
    public sealed class HumanoidEyeTracking
    {
        private const string LeftDownUp = "Left Eye Down-Up";
        private const string LeftInOut = "Left Eye In-Out";
        private const string RightDownUp = "Right Eye Down-Up";
        private const string RightInOut = "Right Eye In-Out";

        private readonly FaceBuildContext _context;
        private readonly AnimationChannelRegistry _channels;
        private readonly GestureProfileConfiguration _gestures;
        private AnimationChannel _muscleChannel;
        private EyePoses _poses;

        internal HumanoidEyeTracking(
            FaceBuildContext context,
            AnimationChannelRegistry channels,
            GestureProfileConfiguration gestures)
        {
            _context = context;
            _channels = channels;
            _gestures = gestures;
        }

        /// <summary>左右上下のSignalをHumanoidの目のMuscleへBindします。</summary>
        public ProfileDraft Bind(
            ProfileDraft profile,
            UnitSignal left,
            UnitSignal right,
            UnitSignal down,
            UnitSignal up)
        {
            EnsureAssets();
            return profile
                .Add(new ClipBind(left, _poses.Left, _muscleChannel))
                .Add(new ClipBind(right, _poses.Right, _muscleChannel))
                .Add(new ClipBind(down, _poses.Down, _muscleChannel))
                .Add(new ClipBind(up, _poses.Up, _muscleChannel));
        }

        /// <summary>
        /// ビルド用Cloneの目の追跡制御を、生成したAnimatorへ渡します。
        /// </summary>
        public HumanoidEyeTracking TakeControl()
        {
            Descriptor().enableEyeLook = false;
            _gestures.TrackingAnimates(AacAv3.Av3TrackingElement.Eyes);
            return this;
        }

        private void EnsureAssets()
        {
            if (_poses != null) return;

            var eyeLook = Descriptor().customEyeLookSettings;
            var lookLeft = Mathf.Abs(SignedEulerAngle(eyeLook.eyesLookingLeft.left.eulerAngles.y));
            var lookRight = Mathf.Abs(SignedEulerAngle(eyeLook.eyesLookingRight.left.eulerAngles.y));
            var lookDown = Mathf.Abs(SignedEulerAngle(eyeLook.eyesLookingDown.left.eulerAngles.x));
            var lookUp = Mathf.Abs(SignedEulerAngle(eyeLook.eyesLookingUp.left.eulerAngles.x));

            _poses = new EyePoses(
                CreatePose(
                    "Eye Left",
                    0f, ToMuscleValue(LeftInOut, lookLeft),
                    0f, ToMuscleValue(RightInOut, -lookLeft)),
                CreatePose(
                    "Eye Right",
                    0f, ToMuscleValue(LeftInOut, -lookRight),
                    0f, ToMuscleValue(RightInOut, lookRight)),
                CreatePose(
                    "Eye Down",
                    ToMuscleValue(LeftDownUp, -lookDown), 0f,
                    ToMuscleValue(RightDownUp, -lookDown), 0f),
                CreatePose(
                    "Eye Up",
                    ToMuscleValue(LeftDownUp, lookUp), 0f,
                    ToMuscleValue(RightDownUp, lookUp), 0f));

            var channels = _channels.Define("Humanoid Eye Muscles", set =>
                set.Channel("Muscles", channel => channel.Include(binding =>
                    binding.type == typeof(Animator) && IsEyeMuscle(binding.propertyName))));
            _muscleChannel = channels["Muscles"];
        }

        private AnimationClip CreatePose(
            string name,
            float leftDownUp,
            float leftInOut,
            float rightDownUp,
            float rightInOut)
        {
            return _context.Aac.NewClip(name).Animating(edit =>
            {
                edit.Animates("", typeof(Animator), LeftDownUp).WithOneFrame(leftDownUp);
                edit.Animates("", typeof(Animator), LeftInOut).WithOneFrame(leftInOut);
                edit.Animates("", typeof(Animator), RightDownUp).WithOneFrame(rightDownUp);
                edit.Animates("", typeof(Animator), RightInOut).WithOneFrame(rightInOut);
            }).Clip;
        }

        private VRCAvatarDescriptor Descriptor()
        {
            var descriptor = _context.AnimatorRoot.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null)
                throw new InvalidOperationException(
                    "Humanoid eye tracking requires a VRCAvatarDescriptor on the Animator root.");
            return descriptor;
        }

        private static float ToMuscleValue(string muscleName, float angleDegrees)
        {
            var muscleIndex = Array.IndexOf(HumanTrait.MuscleName, muscleName);
            if (muscleIndex < 0)
                throw new InvalidOperationException("Humanoid muscle was not found: " + muscleName);

            var limit = angleDegrees >= 0f
                ? HumanTrait.GetMuscleDefaultMax(muscleIndex)
                : Mathf.Abs(HumanTrait.GetMuscleDefaultMin(muscleIndex));
            return Mathf.Approximately(limit, 0f) ? 0f : angleDegrees / limit;
        }

        private static float SignedEulerAngle(float angle) => angle > 180f ? angle - 360f : angle;

        private static bool IsEyeMuscle(string propertyName) =>
            propertyName == LeftDownUp ||
            propertyName == LeftInOut ||
            propertyName == RightDownUp ||
            propertyName == RightInOut;

        private sealed class EyePoses
        {
            internal EyePoses(AnimationClip left, AnimationClip right, AnimationClip down, AnimationClip up)
            {
                Left = left;
                Right = right;
                Down = down;
                Up = up;
            }

            internal AnimationClip Left { get; }
            internal AnimationClip Right { get; }
            internal AnimationClip Down { get; }
            internal AnimationClip Up { get; }
        }
    }
}
