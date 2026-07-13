# Profile Attribute

Tag a method with `[Profile]` and a Unity `ProfilerMarker` gets woven around its body at compile
time.

```csharp
using UnityProfileAttribute;

[Profile]
private void Simulate()
{
    // ...
}
```

The IL that comes out is what you would have written by hand:

```
call     ProfileState::get_Recording()
brfalse  skip
ldsflda  <Profile>Markers::Marker0
call     ProfilerMarker::Begin()
skip:
.try
{
    // your body
}
finally
{
    // End(), if we opened it
}
```

Marker names default to `Type.Method`. Pass a name to override it, which is also how you keep
several overloads aggregating into one row in the profiler:

```csharp
[Profile("Query.Run")]
public void Run<T0>(Action<T0> body) { ... }

[Profile("Query.Run")]
public void Run<T0, T1>(Action<T0, T1> body) { ... }
```

## Install

Via [OpenUPM](https://openupm.com):

```
openupm add com.lgarczyn.profile-attribute
```

Or add the git URL through Package Manager > Add package from git URL:

```
https://github.com/lgarczyn/UnityProfileAttribute.git
```

Requires Unity 2022.3 or newer. Tested on 2022.3 and Unity 6.
Pulls in `com.unity.nuget.mono-cecil`.

To run the package's own tests, add it to `testables` in your project's `Packages/manifest.json`.
Unity will not discover them otherwise:

```json
"testables": [ "com.lgarczyn.profile-attribute" ]
```

## Cost

An idle `ProfilerMarker.Begin()` is not free. It is an unconditional internal call with no managed
side check, so it costs the same whether or not anything is recording. `[Profile]` therefore asks
first, and caches the answer for the frame.

Measured on 2022.3, 20M calls, best of three:

| | editor (Mono) | player (IL2CPP) |
| --- | --- | --- |
| plain method | 2.0 ns | 1.4 ns |
| unconditional marker | 38.7 ns | 9.4 ns |
| `[Profile]`, not recording | 3.9 ns | 2.5 ns |

Most of that 38 ns is Mono in the editor, where an internal call pays a transition wrapper. IL2CPP
compiles it down to a direct call.

And having a lot of markers is not the problem. What costs you is marker *entries* per frame, not
distinct markers, and you need somewhere around 100k entries a frame before any of this is
visible. Where it does bite is a method called per entity rather than per system, so don't put
`[Profile]` inside an inner loop.

## ProfileState

The gate reads `Profiler.enabled` once a frame instead of once a marker, because asking is itself
an internal call. The answer is dropped at the end of every frame from a `PlayerLoop` entry, so it
cannot go stale.

This is deliberately not a flag refreshed at the start of the frame. That version was 0.3 ns
faster and dropped the entire first frame after you hit Record, which is the frame you are looking
at. There is a test for it.

If you drive frames in some way the player loop cannot see, call `ProfileState.Invalidate()` once
a frame yourself, or every marker will freeze in whatever state it last observed.

## deep

`[Profile(deep: true)]` also wraps every call the method makes into your own assemblies, so the
profiler shows which callee is eating the time:

```csharp
[Profile(deep: true)]
private void Tick()
{
    Gather();     // gets its own marker
    Simulate();   // gets its own marker
    list.Add(x);  // BCL, left alone
}
```

Engine, package and BCL calls are skipped on purpose. While recording a marker costs around 65 ns,
so wrapping a 2 ns `List.Add` would measure the measurement rather than your code.

This is a switch you flip while hunting something, not something to leave in. A method making
twenty calls pays for twenty markers on every invocation.

## What gets skipped

`async` methods and iterators. The attribute sits on the stub that builds the state machine, not on
`MoveNext`, so weaving it would time the setup and report nothing for the actual work. You get a
warning instead of a lie.

Burst methods, since Burst compiles to native and cannot call into the profiler.

Methods returning by ref, which cannot cross a `finally`.

## Release builds

`ProfileAttribute` is `[Conditional("ENABLE_PROFILER")]` and the weaver only runs when that define
is set. In a non development build the annotation is not emitted, the weaver does not run, and the
IL is byte for byte what it would have been without the package.

## Failure mode

If weaving ever breaks, it fails quietly: you get a console warning and an assembly with no
markers, rather than a broken build. That is the right call for a profiling tool, but it does mean
a silent failure looks exactly like "I forgot the attribute". `ProfileWeaveTests` asks the IL
directly whether it was woven, so it fails loudly instead. Keep it in your test run.

## License

MIT.
