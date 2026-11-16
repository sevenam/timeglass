using Timeglass.Windows.Autostart;

namespace Timeglass.Windows.IntegrationTests.Autostart;

public class AutostartRegistrationTests
{
    private const string Exe = @"C:\Users\me\AppData\Local\Timeglass\Timeglass.exe";

    private readonly FakeStartupRegistry _key = new();

    private AutostartRegistration Create(string exe = Exe) => new(_key, exe);

    [Fact]
    public void Is_off_without_a_value()
    {
        Create().IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void Enable_registers_the_quoted_path()
    {
        var autostart = Create(@"C:\Program Files\Timeglass\Timeglass.exe");

        autostart.Enable();

        _key.Values.ShouldBe(new Dictionary<string, string>
        {
            [AutostartRegistration.DefaultValueName] = "\"C:\\Program Files\\Timeglass\\Timeglass.exe\"",
        });
        autostart.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void Disable_removes_the_value()
    {
        var autostart = Create();
        autostart.Enable();

        autostart.Disable();

        _key.Values.ShouldBeEmpty();
        autostart.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void Disable_without_a_value_does_nothing()
    {
        var autostart = Create();

        Should.NotThrow(() => autostart.Disable());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetEnabled_switches_either_way(bool enabled)
    {
        var autostart = Create();
        autostart.SetEnabled(!enabled);

        autostart.SetEnabled(enabled);

        autostart.IsEnabled.ShouldBe(enabled);
    }

    [Fact]
    public void State_is_read_from_the_key_each_time()
    {
        var autostart = Create();
        autostart.Enable();

        // Removed outside the app, e.g. by a cleanup tool.
        _key.Values.Clear();

        autostart.IsEnabled.ShouldBeFalse();
    }

    [Theory]
    [InlineData("\"C:\\Users\\me\\AppData\\Local\\Timeglass\\Timeglass.exe\"")]
    [InlineData("\"c:\\users\\ME\\appdata\\local\\timeglass\\TIMEGLASS.EXE\"")]
    [InlineData("C:\\Users\\me\\AppData\\Local\\Timeglass\\Timeglass.exe")]
    [InlineData("  \"C:\\Users\\me\\AppData\\Local\\Timeglass\\Timeglass.exe\" --minimized")]
    public void Commands_that_start_this_exe_count_as_on(string command)
    {
        _key.Values[AutostartRegistration.DefaultValueName] = command;

        Create().IsEnabled.ShouldBeTrue();
    }

    [Theory]
    [InlineData("\"D:\\Old\\Timeglass.exe\"")]
    [InlineData("\"")]
    [InlineData("\"\"")]
    [InlineData("")]
    [InlineData("   ")]
    public void Commands_that_start_something_else_count_as_off(string command)
    {
        _key.Values[AutostartRegistration.DefaultValueName] = command;

        Create().IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void Enable_repoints_a_value_left_by_a_moved_copy()
    {
        _key.Values[AutostartRegistration.DefaultValueName] = "\"D:\\Old\\Timeglass.exe\"";
        var autostart = Create();

        autostart.Enable();

        _key.Values[AutostartRegistration.DefaultValueName].ShouldBe($"\"{Exe}\"");
        autostart.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void Environment_variables_in_the_command_are_expanded()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Timeglass", "Timeglass.exe");
        _key.Values[AutostartRegistration.DefaultValueName] = "\"%LOCALAPPDATA%\\Timeglass\\Timeglass.exe\"";

        Create(exe).IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void Uses_its_own_value_name()
    {
        _key.Values["Other app"] = "\"C:\\other.exe\"";
        var autostart = new AutostartRegistration(_key, Exe, "Timeglass (dev)");

        autostart.Enable();
        autostart.Disable();

        _key.Values.ShouldBe(new Dictionary<string, string> { ["Other app"] = "\"C:\\other.exe\"" });
    }

    [Fact]
    public void Relative_paths_are_made_absolute()
    {
        Path.IsPathFullyQualified(Create("Timeglass.exe").ExecutablePath).ShouldBeTrue();
    }

    [Fact]
    public void Turned_off_in_Task_Manager_counts_as_off()
    {
        var autostart = Create();
        autostart.Enable();

        _key.Approved[AutostartRegistration.DefaultValueName] = [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        autostart.IsEnabled.ShouldBeFalse();
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x03, 0x1a, 0x2b, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x07 }, true)]
    public void Task_Manager_flags_are_read_from_the_first_byte(byte[] approved, bool off)
    {
        AutostartRegistration.IsTurnedOff(approved).ShouldBe(off);
    }

    [Fact]
    public void No_Task_Manager_flag_means_on()
    {
        AutostartRegistration.IsTurnedOff(null).ShouldBeFalse();
        AutostartRegistration.IsTurnedOff([]).ShouldBeFalse();
    }

    [Fact]
    public void Enable_clears_a_Task_Manager_off_flag()
    {
        var autostart = Create();
        autostart.Enable();
        _key.Approved[AutostartRegistration.DefaultValueName] = [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        autostart.Enable();

        _key.Approved.ShouldBeEmpty();
        autostart.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void Disable_also_removes_the_Task_Manager_flag()
    {
        var autostart = Create();
        autostart.Enable();
        _key.Approved[AutostartRegistration.DefaultValueName] = [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        autostart.Disable();

        _key.Values.ShouldBeEmpty();
        _key.Approved.ShouldBeEmpty();
    }

    [Fact]
    public void Registered_executable_is_whatever_the_value_starts()
    {
        var autostart = Create();
        autostart.RegisteredExecutable.ShouldBeNull();

        _key.Values[AutostartRegistration.DefaultValueName] = "\"D:\\Old\\Timeglass.exe\" --minimized";
        autostart.RegisteredExecutable.ShouldBe(@"D:\Old\Timeglass.exe");

        _key.Values[AutostartRegistration.DefaultValueName] = "\"\"";
        autostart.RegisteredExecutable.ShouldBeNull();
    }

    [Fact]
    public void Registered_executable_ignores_the_Task_Manager_flag()
    {
        var autostart = Create();
        autostart.Enable();
        _key.Approved[AutostartRegistration.DefaultValueName] = [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        autostart.RegisteredExecutable.ShouldBe(Exe);
    }

    [Fact]
    public void Adopt_points_another_copys_value_at_this_exe()
    {
        _key.Values[AutostartRegistration.DefaultValueName] = "\"D:\\Old\\Timeglass.exe\"";
        var autostart = Create();

        autostart.Adopt().ShouldBeTrue();

        _key.Values[AutostartRegistration.DefaultValueName].ShouldBe($"\"{Exe}\"");
        autostart.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void Adopt_keeps_autostart_off_when_Task_Manager_turned_it_off()
    {
        _key.Values[AutostartRegistration.DefaultValueName] = "\"D:\\Old\\Timeglass.exe\"";
        _key.Approved[AutostartRegistration.DefaultValueName] = [0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var autostart = Create();

        autostart.Adopt().ShouldBeTrue();

        _key.Approved.ShouldContainKey(AutostartRegistration.DefaultValueName);
        autostart.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void Adopt_does_not_turn_autostart_on()
    {
        Create().Adopt().ShouldBeFalse();

        _key.Values.ShouldBeEmpty();
    }

    [Fact]
    public void Adopt_leaves_a_value_for_this_exe_as_it_is()
    {
        var command = "\"c:\\users\\ME\\appdata\\local\\timeglass\\TIMEGLASS.EXE\" --minimized";
        _key.Values[AutostartRegistration.DefaultValueName] = command;

        Create().Adopt().ShouldBeFalse();

        _key.Values[AutostartRegistration.DefaultValueName].ShouldBe(command);
    }

    private sealed class FakeStartupRegistry : IStartupRegistry
    {
        public Dictionary<string, string> Values { get; } = [];

        public Dictionary<string, byte[]> Approved { get; } = [];

        public string? GetRunValue(string name) => Values.GetValueOrDefault(name);

        public void SetRunValue(string name, string command) => Values[name] = command;

        public void DeleteRunValue(string name) => Values.Remove(name);

        public byte[]? GetApprovedValue(string name) => Approved.GetValueOrDefault(name);

        public void DeleteApprovedValue(string name) => Approved.Remove(name);
    }
}
