namespace RO2.Server.Zone.Services;

/// <summary>
/// A simulation subsystem driven by the central game loop instead of its own timer. The loop calls
/// <see cref="Tick"/> once per fixed step with the step size in seconds.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Tick"/> runs on the loop thread and must not block. Network I/O started from it must not be
/// awaited there.
/// </para>
/// <para>
/// A subsystem that acts only a few times per second keeps its own accumulator and returns early until its
/// interval has elapsed.
/// </para>
/// </remarks>
public interface ITickable
{
 /// <summary>Advances the subsystem by <paramref name="deltaSeconds"/> of simulated time.</summary>
    void Tick(double deltaSeconds);

 /// <summary>Name shown in game-loop telemetry and error logs.</summary>
    string Name => GetType().Name;
}
