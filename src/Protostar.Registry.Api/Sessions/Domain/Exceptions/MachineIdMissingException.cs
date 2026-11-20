namespace Protostar.Registry.Api.Sessions;

/// <summary>Raised when a machine is registered under the all-zeros id.</summary>
/// <remarks>
/// Ordinary input a client can send (an uninitialised GUID), so the application service translates it to a
/// 400 rather than letting it surface as an error.
/// </remarks>
public sealed class MachineIdMissingException()
    : Exception("A machine id cannot be all zeros. Generate a random id once (a v4 UUID) and keep it.");
