namespace AngstromCommander.Server.Auth;

/// <summary>
/// Whether this environment accepts new accounts. Closed by default, like the CORS
/// origins: an environment that has not deliberately opened registration refuses it —
/// accounts are the key to everything the relay can cost, so standing environments
/// stay private until someone decides otherwise. Development opts in via
/// appsettings.Development.json; a deployed stamp flips Registration__Enabled without
/// a deploy.
/// </summary>
internal sealed class RegistrationOptions
{
    public const string SectionName = "Registration";

    public bool Enabled { get; set; }
}
