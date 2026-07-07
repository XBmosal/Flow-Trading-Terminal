```

BenchmarkDotNet v0.13.12, Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 8.0.422
  [Host] : .NET 8.0.28 (8.0.2826.26413), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  Dry    : .NET 8.0.28 (8.0.2826.26413), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI

Job=Dry  IterationCount=1  LaunchCount=1  
RunStrategy=ColdStart  UnrollFactor=1  WarmupCount=1  

```
| Method                           | Mean      | Error | Allocated |
|--------------------------------- |----------:|------:|----------:|
| BarBuilding_TimeBars             |  3.667 ms |    NA |     880 B |
| Footprint_Aggregation            |  3.382 ms |    NA |    5736 B |
| Cvd_Update                       |  1.738 ms |    NA |     776 B |
| VolumeProfile_Update             |  2.873 ms |    NA |    1816 B |
| BigTrades_Classify_And_Aggregate | 19.465 ms |    NA |  907024 B |
| Dom_Ladder_Build                 |  9.831 ms |    NA |   13184 B |
