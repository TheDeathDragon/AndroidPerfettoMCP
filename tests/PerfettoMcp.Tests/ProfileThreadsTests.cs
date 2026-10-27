using System.Text.Json.Nodes;
using PerfettoMcp.Tools;
using Xunit;

namespace PerfettoMcp.Tests;

public class ProfileThreadsTests
{
    [Fact]
    public void ParsesFlatAndChildrenReports()
    {
        // simpleperf report of a CachedAppOptimizer compaction, trimmed.
        const string flat = """
            Cmdline: /system/bin/simpleperf record -t 1303 -g --duration 35 -o /data/local/tmp/cao.data
            Arch: arm64
            Event: cpu-cycles (type 0, config 0x0, config1 0x0, config2: 0x0)
            Samples: 11833
            Event count: 4308422402

            Overhead  Sample  Shared Object                                    Symbol
            21.23%    12432   [kernel.kallsyms]                                LZ4_compress_fast_extState
            11.96%    7008    [kernel.kallsyms]                                _raw_spin_unlock_irq
            2.86%     1673    /system/lib64/libart.so                          art::gc::Heap::Trim(art::Thread *)
            """;
        ProfileThreadsTool.ReportTable t = ProfileThreadsTool.Parse(flat);
        Assert.Equal(11833, t.Samples);
        Assert.Equal(4308422402, t.EventCount);
        Assert.Equal(new[] { "Overhead", "Sample", "Shared Object", "Symbol" }, t.Columns);
        Assert.Equal(3, t.Rows.Count);
        Assert.Equal("art::gc::Heap::Trim(art::Thread *)", t.Rows[2][3]);

        const string children = """
            Samples: 11833
            Children  Self    Sample  Shared Object      Symbol
            100.00%   0.00%   0       [kernel.kallsyms]  el0t_64_sync
            99.51%    0.00%   0       [kernel.kallsyms]  memory_reclaim
            53.90%    0.73%   424     [kernel.kallsyms]  swap_writeout
            """;
        JsonObject json = ProfileThreadsTool.Parse(children).ToJson(10, skipFullCover: true);
        Assert.Equal(2, json["rows"]!.AsArray().Count); // the 100% entry frame is dropped
        Assert.Equal("memory_reclaim", json["rows"]![0]![4]!.GetValue<string>());
    }
}
