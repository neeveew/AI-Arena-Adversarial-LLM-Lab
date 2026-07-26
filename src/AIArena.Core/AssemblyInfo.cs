using System.Runtime.CompilerServices;

// Allow the core test project to exercise internal helpers (e.g. golden prompt builders).
[assembly: InternalsVisibleTo("AIArena.Tests")]
