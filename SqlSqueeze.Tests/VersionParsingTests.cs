// SELECT @@VERSION parsing: the dash that precedes the version number is not
// always the first dash in the string.

using System;
using SqlSqueeze.Core;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class VersionParsingTests
    {
        [Theory]
        [InlineData("Microsoft SQL Server 2008 (SP1) - 10.0.2531.0 (Intel X86)   Mar 29 2009 10:27:29   Copyright (c) 1988-2008 Microsoft Corporation  Developer Edition on Windows NT 5.1 <X86> (Build 2600: Service Pack 3) ", 10)]
        [InlineData("Microsoft SQL Server 2019 (RTM-CU18) (KB5017593) - 15.0.4261.1 (X64) \n\tSep 12 2022 15:07:06 \n\tDeveloper Edition (64-bit) on Linux (Ubuntu 20.04.5 LTS) <X64>", 15)]
        [InlineData("Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64) \n\tAug 26 2026 11:02:22 \n\tCopyright (C) 2022 Microsoft Corporation\n\tDeveloper Edition (64-bit) on Linux (Ubuntu 24.04.4 LTS) <X64>", 16)]
        [InlineData("Microsoft SQL Server 2022 (RTM-GDR) (KB5046861) - 16.0.1135.2 (X64) Developer Edition", 16)]
        [InlineData("Microsoft SQL Server 2025 (RTM) - 17.0.1000.7 (X64) Express Edition", 17)]
        public void TheMajorVersionIsTheNumberAfterTheSpacedDash(string version, int expected)
        {
            Assert.Equal(expected, SqlThread.GetMajorVersionNumber(version));
        }

        [Theory]
        [InlineData("")]
        [InlineData("not a version")]
        [InlineData("Microsoft SQL Server - abc.def")]
        public void AnUnrecognisedVersionString_IsAClearError(string version)
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() => SqlThread.GetMajorVersionNumber(version));
            Assert.Contains("unexpected version string", e.Message);
        }
    }
}
