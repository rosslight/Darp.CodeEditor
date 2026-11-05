namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// Marks benchmarks of code that the target branch does not have yet. A comparison measures them in the current
/// version only and lists them as not compared, and the other benchmarks still compare. Their file is left out of the
/// baseline's build with <c>#if !BENCHMARK_BASELINE</c>. Remove both once the target branch has the code.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class NotInBaselineAttribute : Attribute;
