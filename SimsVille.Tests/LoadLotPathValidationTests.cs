/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

// Unit tests for VMHouseXmlValidator.IsValidHouseXml (reeims-e54 — security).
//
// Done condition: 5+ rejection cases + 2 acceptance cases.
//
// VMHouseXmlValidator is included directly via <Compile Include> in the test
// project (it has no MonoGame deps), so tests call the real validator with no
// mocking or reimplementation.

using FSO.SimAntics.NetPlay.Model.Commands;
using Xunit;

namespace SimsVille.Tests
{
    public class LoadLotPathValidationTests
    {
        // -------------------------------------------------------
        // Rejection cases (5 minimum per done condition)
        // -------------------------------------------------------

        [Fact]
        public void Reject_Null()
        {
            Assert.False(VMHouseXmlValidator.IsValidHouseXml(null));
        }

        [Fact]
        public void Reject_Empty()
        {
            Assert.False(VMHouseXmlValidator.IsValidHouseXml(""));
        }

        [Fact]
        public void Reject_DotDotSlashPrefix()
        {
            // Classic traversal: ../etc/passwd
            Assert.False(VMHouseXmlValidator.IsValidHouseXml("../etc/passwd"));
        }

        [Fact]
        public void Reject_DotDotInMiddle()
        {
            // Embedded traversal: normal\..\escape
            Assert.False(VMHouseXmlValidator.IsValidHouseXml("normal\\..\\escape"));
        }

        [Fact]
        public void Reject_AbsoluteUnixPath()
        {
            // Rooted absolute path
            Assert.False(VMHouseXmlValidator.IsValidHouseXml("/tmp/evil"));
        }

        [Fact]
        public void Reject_TildePrefix()
        {
            // Home-directory shorthand
            Assert.False(VMHouseXmlValidator.IsValidHouseXml("~evil"));
        }

        [Fact]
        public void Reject_ForwardSlashSeparator()
        {
            // Bare slash without .., but still a path separator
            Assert.False(VMHouseXmlValidator.IsValidHouseXml("houses/house1.xml"));
        }

        [Fact]
        public void Reject_BackslashSeparator()
        {
            // Windows-style separator — still a path separator
            Assert.False(VMHouseXmlValidator.IsValidHouseXml("houses\\house1.xml"));
        }

        [Fact]
        public void Reject_DotDotAloneAsFilename()
        {
            // Edge case: ".." by itself
            Assert.False(VMHouseXmlValidator.IsValidHouseXml(".."));
        }

        // -------------------------------------------------------
        // Acceptance cases (2 minimum per done condition)
        // -------------------------------------------------------

        [Fact]
        public void Accept_House1()
        {
            Assert.True(VMHouseXmlValidator.IsValidHouseXml("house1.xml"));
        }

        [Fact]
        public void Accept_House2()
        {
            Assert.True(VMHouseXmlValidator.IsValidHouseXml("house2.xml"));
        }

        [Fact]
        public void Accept_ArbitraryBareFilename()
        {
            // Any bare filename with no path structure should be accepted
            Assert.True(VMHouseXmlValidator.IsValidHouseXml("my_lot.xml"));
        }
    }
}
