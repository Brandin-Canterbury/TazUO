using ClassicUO.Game.Managers.Crafting;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Game.Managers.Crafting
{
    public class CraftGumpLayoutTests
    {
        // Abridged PacketGumpText of a ServUO blacksmithy CraftGump showing the Bladed category, with the
        // "You create the item." notice. Button IDs follow CraftGump.GetButtonID(type, index) = 1 + type + index * 7.
        private const string BLACKSMITH_GUMP =
            "page 0\n" +
            "resizepic 0 0 5054 530 497\n" +
            "xmfhtmlgumpcolor 10 12 510 20 1044002 0 0 32767\n" +
            "xmfhtmlgumpcolor 10 37 200 22 1044010 0 0 32767\n" +
            "xmfhtmlgumpcolor 215 37 305 22 1044011 0 0 32767\n" +
            "button 15 442 4017 4019 1 0 0\n" +
            "xmfhtmlgumpcolor 50 445 150 18 1011441 0 0 32767\n" +
            "button 270 442 4005 4007 1 0 21\n" +
            "xmfhtmlgumpcolor 305 445 150 18 1044013 0 0 32767\n" +
            "button 15 342 4005 4007 1 0 14\n" +
            "xmfhtmlgumpcolor 50 345 150 18 1044259 0 0 32767\n" +
            "xmfhtmlgumpcolor 170 295 350 40 1044154 0 0 32767\n" +
            "button 15 60 4005 4007 1 0 22\n" +
            "xmfhtmlgumpcolor 50 63 150 18 1044014 0 0 32767\n" +
            "button 15 80 4005 4007 1 0 1\n" +
            "xmfhtmlgumpcolor 50 83 150 18 1111704 0 0 32767\n" +
            "button 15 100 4005 4007 1 0 8\n" +
            "xmfhtmlgumpcolor 50 103 150 18 1011081 0 0 32767\n" +
            "page 1\n" +
            "button 220 60 4005 4007 1 0 2\n" +
            "xmfhtmlgumpcolor 255 63 220 18 1023921 0 0 32767\n" +
            "button 480 60 4011 4012 1 0 3\n" +
            "button 220 80 4005 4007 1 0 9\n" +
            "xmfhtmlgumpcolor 255 83 220 18 #1095362 0 0 32767\n" +
            "button 480 80 4011 4012 1 0 10\n";

        [Fact]
        public void Parse_RecognizesCraftGumpTitleAndNotice()
        {
            CraftGumpLayout layout = CraftGumpLayout.Parse(BLACKSMITH_GUMP);

            layout.IsCraftGump.Should().BeTrue();
            layout.TitleCliloc.Should().Be(1044002);
            layout.NoticeCliloc.Should().Be(1044154);
            layout.NoticeLacksMana.Should().BeFalse();
        }

        [Fact]
        public void TryFindButton_FindsCategoryAndItemButtons()
        {
            CraftGumpLayout layout = CraftGumpLayout.Parse(BLACKSMITH_GUMP);

            layout.TryFindButton(1011081, CraftButtonKind.Group, out int group).Should().BeTrue();
            group.Should().Be(8);

            layout.TryFindButton(1023921, CraftButtonKind.Item, out int dagger).Should().BeTrue();
            dagger.Should().Be(2);

            layout.TryFindButton(1095362, CraftButtonKind.Item, out int gargishDagger).Should().BeTrue();
            gargishDagger.Should().Be(9);
        }

        [Fact]
        public void TryFindButton_RejectsWrongKindAndMissingRows()
        {
            CraftGumpLayout layout = CraftGumpLayout.Parse(BLACKSMITH_GUMP);

            layout.TryFindButton(1011081, CraftButtonKind.Item, out _).Should().BeFalse();
            layout.TryFindButton(1095373, CraftButtonKind.Item, out _).Should().BeFalse();

            // "Smelt item" is a misc button (type 6), never a category or item.
            layout.TryFindButton(1044259, CraftButtonKind.Group, out _).Should().BeFalse();
        }

        [Fact]
        public void Parse_OtherGumpIsNotCraftGump()
        {
            CraftGumpLayout layout = CraftGumpLayout.Parse("page 0\nbutton 10 10 1 2 1 0 5\ntext 30 10 0 0\n");

            layout.IsCraftGump.Should().BeFalse();
        }

        [Fact]
        public void Parse_DetectsPlainTextManaNotice()
        {
            const string text = "<BASEFONT COLOR=#FFFFFF>You lack the required mana to make that.</BASEFONT>\n" + BLACKSMITH_GUMP;

            CraftGumpLayout.Parse(text).NoticeLacksMana.Should().BeTrue();
        }

        [Theory]
        [InlineData(1044154, (int)CraftResult.Success)]
        [InlineData(1044043, (int)CraftResult.Failed)]
        [InlineData(1044037, (int)CraftResult.NoMaterials)]
        [InlineData(1044364, (int)CraftResult.NoMaterials)]
        [InlineData(1044267, (int)CraftResult.NeedStation)]
        [InlineData(1044038, (int)CraftResult.ToolBroken)]
        [InlineData(1044270, (int)CraftResult.Recycled)]
        [InlineData(1044272, (int)CraftResult.RecycleRefused)]
        [InlineData(1, (int)CraftResult.None)]
        public void Classify_MapsServerClilocs(int cliloc, int expected)
        {
            CraftMessages.Classify(cliloc).Should().Be((CraftResult)expected);
        }
    }
}
