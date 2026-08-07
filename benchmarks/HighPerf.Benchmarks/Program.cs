using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(HighPerf.Benchmarks.GeoBenchmarks).Assembly).Run(args);
