using System.Text.Json.Nodes;
using WinUse.Tools;

namespace WinUse.Core.Tests.Tools;

public class ToolRegistryTests
{
    [Fact]
    public void AllSpecifiedToolsAreRegistered()
    {
        var names = ToolDispatcher.DiscoverToolNames();   // static, reflection
        string[] expected = ["screenshot","list_windows","get_ui_tree","get_dom","get_element","get_text","get_table","get_list_items",
            "act","find","check","click","hover","drag","scroll","type_text","press_keys",
            "set_text","select_item","set_checked","focus","invoke",
            "window_activate","window_close","window_set_state","window_move","window_exists","wait_for","sleep",
            "clipboard_get","clipboard_set","clipboard_set_image","paste_text","paste_image",
            "process_run","process_kill","process_exists",
            "file_exists","read_text","write_text","append_text","copy","move","delete","list_dir","zip","unzip",
            "assert_text","assert_exists","assert_window","assert_file",
            "scenario_save","scenario_list","scenario_run"];
        Assert.Empty(expected.Except(names));
    }

    [Fact]
    public void ToolResult_Ok_MergesStepId()
    {
        var json = ToolResult.Ok(new { Text = "hi", count = 2 }, "s7");
        var o = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("s7", o["step_id"]!.GetValue<string>());
        Assert.Equal("hi", o["text"]!.GetValue<string>());
        Assert.Equal(2, o["count"]!.GetValue<int>());
        Assert.Equal("step_id", o.First().Key);
    }

    [Fact]
    public void ToolResult_Error_IsErrorWithStepId()
    {
        var r = ToolResult.Error("boom", "s3");
        Assert.True(r.IsError);
        var text = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(r.Content)).Text;
        var o = JsonNode.Parse(text)!.AsObject();
        Assert.Equal("s3", o["step_id"]!.GetValue<string>());
        Assert.Equal("boom", o["error"]!.GetValue<string>());
    }
}
