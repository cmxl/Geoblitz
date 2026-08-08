using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Geoblitz.Benchmarks.GeoBenchmarks).Assembly).Run(args);
