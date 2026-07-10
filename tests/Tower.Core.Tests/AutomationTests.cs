using Tower.Core.Automations;
using Xunit;
namespace Tower.Core.Tests;
public class AutomationTests {
    [Fact] public void Actions_round_trip_through_json() {
        var actions = new List<AutomationAction> {
            new("tuya", "dev1", "Baby Room Bulb", true, null),
            new("tuya", "dev2", "Baby Room AC", true, 27),
            new("pi", "atomtv", "AtomTV", false, null),
        };
        var json = AutomationService.SerializeActions(actions);
        Assert.Equal(actions, AutomationService.ParseActions(json));
    }
    [Fact] public void Malformed_json_parses_to_empty_list() {
        Assert.Empty(AutomationService.ParseActions("not json"));
        Assert.Empty(AutomationService.ParseActions(""));
    }
}
