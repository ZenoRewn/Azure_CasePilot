using CasePilot.App.Mvvm;

namespace CasePilot.App.Tests;

[TestClass]
public class MvvmTests
{
    sealed class Vm : ObservableObject
    {
        string _name = "";
        public string Name { get => _name; set => SetProperty(ref _name, value); }
    }

    [TestMethod]
    public void SetProperty_RaisesOnlyOnChange()
    {
        var vm = new Vm(); var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.Name = "a"; vm.Name = "a"; vm.Name = "b";
        CollectionAssert.AreEqual(new[] { "Name", "Name" }, raised);
    }

    [TestMethod]
    public void RelayCommand_ExecutesAndHonorsCanExecute()
    {
        var n = 0; var can = false;
        var cmd = new RelayCommand(() => n++, () => can);
        Assert.IsFalse(cmd.CanExecute(null));
        can = true;
        var changed = 0; cmd.CanExecuteChanged += (_, _) => changed++;
        cmd.NotifyCanExecuteChanged();
        Assert.AreEqual(1, changed);
        Assert.IsTrue(cmd.CanExecute(null));
        cmd.Execute(null);
        Assert.AreEqual(1, n);
    }

    [TestMethod]
    public void RelayCommandT_PassesParameter()
    {
        string? got = null;
        var cmd = new RelayCommand<string>(s => got = s);
        cmd.Execute("x");
        Assert.AreEqual("x", got);
    }

    [TestMethod]
    public async Task AsyncRelayCommand_IsRunningBlocksReentry()
    {
        var tcs = new TaskCompletionSource(); var runs = 0;
        var cmd = new AsyncRelayCommand(async () => { runs++; await tcs.Task; });
        var t = cmd.ExecuteAsync();
        Assert.IsTrue(cmd.IsRunning);
        Assert.IsFalse(cmd.CanExecute(null));
        cmd.Execute(null);
        Assert.AreEqual(1, runs);
        tcs.SetResult(); await t;
        Assert.IsFalse(cmd.IsRunning);
        Assert.IsTrue(cmd.CanExecute(null));
    }

    [TestMethod]
    public async Task AsyncRelayCommand_CapturesException()
    {
        var cmd = new AsyncRelayCommand(() => throw new InvalidOperationException("boom"));
        Exception? seen = null; cmd.Failed += e => seen = e;
        await cmd.ExecuteAsync();
        Assert.AreEqual("boom", seen?.Message);
        Assert.IsFalse(cmd.IsRunning);
    }

    [TestMethod]
    public async Task AsyncRelayCommand_WithoutSubscriber_ReportsToUnobserved_ExceptCancel()
    {
        var seen = new List<Exception>();
        AsyncRelayCommand.Unobserved = seen.Add;
        try
        {
            await new AsyncRelayCommand(() => throw new InvalidOperationException("boom")).ExecuteAsync();
            await new AsyncRelayCommand(() => throw new OperationCanceledException()).ExecuteAsync();
            var handled = new AsyncRelayCommand(() => throw new InvalidOperationException("x"));
            handled.Failed += _ => { };
            await handled.ExecuteAsync();
        }
        finally { AsyncRelayCommand.Unobserved = null; }
        Assert.AreEqual(1, seen.Count);
        Assert.AreEqual("boom", seen[0].Message);
    }
}
