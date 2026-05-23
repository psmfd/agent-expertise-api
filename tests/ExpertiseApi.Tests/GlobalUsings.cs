global using Xunit;
global using Microsoft.EntityFrameworkCore;
// AwesomeAssertions 9.x renamed the namespace (`FluentAssertions` →
// `AwesomeAssertions`); hoisting the import here lets every test file drop
// its per-file using and stay namespace-agnostic at the call sites.
global using AwesomeAssertions;
