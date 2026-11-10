# Face Expression Framework API

## Processing model

Input values are calculated independently from animation binding. Avatar code chooses the input paths, rules, clips, and channels. A `UnitSignal` represents a value in 0–1 and can be bound to animation. Gesture profiles contain reusable bindings; a gesture copies the default profile and changes only its bindings or base pose.

## Signals

```csharp
RawSignal  face.Signals.Input(string path);
UnitSignal face.Signals.UnitInput(string path);
RawSignal  RawSignal.Smooth(float local, float remote);
UnitSignal RawSignal.ToUnit(float start, float end);
BipolarSignal RawSignal.ToBipolar(float negativeEnd, float positiveEnd);
UnitSignal BipolarSignal.Positive { get; }
UnitSignal BipolarSignal.Negative { get; }
BipolarSignal UnitSignal.Prioritize(UnitSignal priority);
UnitSignal UnitSignal.Max(UnitSignal other);
UnitSignal UnitSignal.SuppressBy(UnitSignal condition, float startsAt = 0f, float fullyAt = 1f);
UnitSignal UnitSignal.SuppressBy(params UnitSignal[] conditions);
UnitSignal UnitSignal.OnlyWhenIntEquals(string parameterName, int value);
void face.Signals.OverrideInputDefault(string path, float value);
```

`ToUnit` maps start to 0 and end to 1, including reversed ranges such as `(0, -1)`, and clamps outside the interval. `ToBipolar` provides both sides without a separate `Split` operation. Multiple suppression conditions use the strongest suppression. Input paths are assumed valid. Call `OverrideInputDefault` before `Input` for inputs such as Lapwing's EyeLid whose neutral value is not zero. The input smoother starts from that same default.

Call `Smooth` on an `Input` before `ToUnit` or `ToBipolar` to smooth that raw parameter once. `UnitInput(path)` is the unsmoothed shortcut for `Input(path).ToUnit(0f, 1f)`. All downstream calculations and bindings use the same smoothed input. Inputs without `Smooth` remain unsmoothed; bindings do not add smoothing.

## Bindings and profiles

```csharp
new ClipBind(UnitSignal signal, AnimationClip clip, AnimationChannel channel = null);
SurfaceBind SurfaceBind.Create(UnitSignal x, UnitSignal y, AnimationChannel channel,
    Action<SurfaceDraft> configure);
SurfaceDraft SurfaceDraft.At(float x, float y, AnimationClip clip);
ExpressionProfile face.Expressions.Create(string name,
    Action<ProfileDraft> configure);
ProfileDraft ProfileDraft.Add(ClipBind binding);
ProfileDraft ProfileDraft.Add(SurfaceBind binding);
ProfileDraft ProfileDraft.ReplaceClip(ClipBind binding, AnimationClip clip);
ProfileDraft ProfileDraft.Without(ClipBind binding);
ProfileDraft ProfileDraft.Without(SurfaceBind binding);
ProfileDraft ProfileDraft.Without(AnimationChannel channel);
ProfileDraft ProfileDraft.WithoutBindingsUsing(UnitSignal signal);
ProfileDraft ProfileDraft.BaseClip(AnimationClip clip);
ProfileDraft ProfileDraft.BaseClip(AnimationClip clip, AnimationChannel channel);
GestureProfileConfiguration face.Gestures.Default(ExpressionProfile profile);
GestureProfileConfiguration face.Gestures.For(AacAv3.Av3Gesture gesture,
    Action<ProfileDraft> changes);
GestureProfileConfiguration face.Gestures.TransitionDuration(float seconds);
void face.Build();
```

Write ordinary bindings as `profile.Add(new ClipBind(...))` or `profile.Add(SurfaceBind.Create(...))`. Omitting the `ClipBind` channel applies the clip to every channel, including `Default`, in the profile's channel set. If the profile has no bindings yet, the framework must have exactly one defined channel set. Keep a separate `ClipBind` or `SurfaceBind` variable only when another profile needs to identify that binding. `For` copies the default profile. `ReplaceClip` changes exactly the selected 1D binding, even if other bindings use the same signal. `WithoutBindingsUsing` deliberately removes every binding using a signal, including 2D surfaces. `Without(channel)` removes parameter-driven 1D and 2D bindings on that channel while preserving the gesture's base clip. `BaseClip(clip)` uses the channel sets of both 1D bindings and 2D surfaces to define the resting pose; `BaseClip(clip, channel)` targets one channel. Curves are split by user-defined animation channels, and a curve assigned to two channels causes a build error.

`ReplaceTrackingWhileSpeaking(channel)` gates tracking bindings on the selected channel and includes only Viseme curves classified into that same channel. Define channel selectors that cover the relevant Viseme curves as well as tracking curves. If no Viseme curve belongs to a selected channel, the build fails instead of silently disabling lip sync. Other channels' Viseme curves are not written by the generated Viseme layer.

```csharp
var signals = face.Signals;
var smileFrown = signals.Input("v2/SmileFrown").Smooth(0.1f, 0.7f)
    .ToBipolar(-1f, 1f);
var mouthOpen = signals.Input("v2/MouthOpen").Smooth(0.1f, 0.7f)
    .ToUnit(0f, 1f)
    .SuppressBy(smileFrown.Negative);
var jaw = new ClipBind(signals.Input("v2/JawOpen").Smooth(0.1f, 0.7f)
    .ToUnit(0f, 1f), jawClip, mouthChannel);
var normal = face.Expressions.Create("Normal", p => p.Add(jaw));
face.Gestures.Default(normal)
    .For(AacAv3.Av3Gesture.RockNRoll, p => p.ReplaceClip(jaw, troubledJawClip));
face.Build();
```

Lip sync, Humanoid Eyes, and the optional Modular Avatar installer continue to consume completed signals or generated controllers. The core framework does not hard-code VRCFT parameter names or face parts.
