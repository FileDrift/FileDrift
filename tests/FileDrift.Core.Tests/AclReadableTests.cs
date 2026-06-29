// SPDX-License-Identifier: GPL-3.0-or-later
using FileDrift.Core.Engine;
using Xunit;

namespace FileDrift.Core.Tests;

public class AclReadableTests
{
    [Theory]
    [InlineData("A;;FA;;;BA", "Allow Administrators: Full control")]
    [InlineData("A;;FR;;;WD", "Allow Everyone: Read")]
    [InlineData("A;;FRFX;;;BU", "Allow Users: Read, Execute")]
    [InlineData("D;;FW;;;BG", "Deny Guests: Write")]
    [InlineData("A;;0x1200a9;;;SY", "Allow SYSTEM: Read & execute")]
    [InlineData("A;;FA;;;S-1-5-32-544", "Allow Administrators: Full control")]
    public void Ace_translates_known_forms(string ace, string expected) =>
        Assert.Equal(expected, AclReadable.Ace(ace));

    [Fact]
    public void Ace_unresolvable_sid_falls_back_to_raw_form()
    {
        var result = AclReadable.Ace("A;;FA;;;S-1-5-21-9999-8888-7777-4444");
        Assert.StartsWith("Allow ", result);
        Assert.EndsWith(": Full control", result);
    }
}
