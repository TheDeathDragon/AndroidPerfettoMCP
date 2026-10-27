using System;
using System.IO;
using System.Threading.Tasks;
using PerfettoMcp.Device;
using PerfettoMcp.Mcp;
using Xunit;

namespace PerfettoMcp.Tests;

public class DeviceStepsTests
{
    // A stand-in for adb that prints a fixed line, whatever it is asked to run on the "device".
    private static string FakeAdb(string dir, string output)
    {
        if (OperatingSystem.IsWindows())
        {
            string cmd = Path.Combine(dir, "fake-adb.cmd");
            File.WriteAllText(cmd, $"@echo off\r\necho {output}\r\n");
            return cmd;
        }
        string sh = Path.Combine(dir, "fake-adb");
        File.WriteAllText(sh, $"#!/bin/sh\necho {output}\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    private static async Task<int> WaitWith(string output, int limitS)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm-adb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Adb adb = new(FakeAdb(dir, output));
            return await DeviceSteps.WaitWhileAsync(adb, "serial", "true", limitS, RequestContext.None, "test");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ConditionClearedReturnsSecondsWaited()
    {
        Assert.Equal(7, await WaitWith("done=7", 5));
    }

    [Fact]
    public async Task DeviceLoopRunningOutIsNotSuccess()
    {
        // The device-side loop gave up while the condition still held: keep trying until the deadline, then -1
        // (record_boot relies on this to report a device that never finished booting).
        Assert.Equal(-1, await WaitWith("still=3", 3));
    }

    [Theory]
    // perfetto started at 12.00 s uptime, 90 s trace, now 60.5 s: 41.5 s left on the host before polling the device.
    [InlineData("start=1200 tck=100 up=60.5\n", 90, 41.5)]
    [InlineData("start=1200 tck=100 up=150.0\n", 90, 0)]
    [InlineData("", 90, 0)]
    public void BootTraceEndFromPerfettoStartTime(string stdout, double traceS, double expected) =>
        Assert.Equal(expected, PerfettoMcp.Tools.RecordBootTool.SecondsUntilTraceEnd(stdout, traceS), 3);
}
