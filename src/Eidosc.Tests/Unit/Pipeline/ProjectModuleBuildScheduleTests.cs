using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class ProjectModuleBuildScheduleTests
{
    [Fact]
    public void FromGraphSnapshot_UsesTopologicalLayersAsParallelWorkLayers()
    {
        var graph = new ModuleDependencyGraph();
        graph.RegisterModuleIdentity("a.eidos", "A");
        graph.RegisterModuleIdentity("b.eidos", "B");
        graph.RegisterModuleIdentity("c.eidos", "C");
        graph.RegisterModuleIdentity("d.eidos", "D");
        graph.AddDependency("B", "A");
        graph.AddDependency("C", "A");
        graph.AddDependency("D", "B");
        graph.AddDependency("D", "C");
        var snapshot = ProjectModuleGraphSnapshot.FromDependencyGraph(graph);

        var schedule = ProjectModuleBuildSchedule.FromGraphSnapshot(snapshot);

        Assert.Equal(3, schedule.Layers.Count);
        Assert.Equal(2, schedule.MaxParallelWidth);
        Assert.Equal(["A"], schedule.Layers[0].Modules.Select(static module => module.ModuleKey));
        Assert.Equal(["B", "C"], schedule.Layers[1].Modules.Select(static module => module.ModuleKey));
        Assert.Equal(["D"], schedule.Layers[2].Modules.Select(static module => module.ModuleKey));
        Assert.Equal(["A"], schedule.Layers[1].Modules[0].Dependencies);
        Assert.Equal(["D"], schedule.Layers[1].Modules[0].Dependents);
    }

    [Fact]
    public void FromGraphSnapshot_PreservesIsolatedModulesAsParallelLayer()
    {
        var graph = new ModuleDependencyGraph();
        graph.RegisterModuleIdentity("b.eidos", "B");
        graph.RegisterModuleIdentity("a.eidos", "A");
        var snapshot = ProjectModuleGraphSnapshot.FromDependencyGraph(graph);

        var schedule = ProjectModuleBuildSchedule.FromGraphSnapshot(snapshot);

        var layer = Assert.Single(schedule.Layers);
        Assert.Equal(2, schedule.MaxParallelWidth);
        Assert.Equal(["A", "B"], layer.Modules.Select(static module => module.ModuleKey));
    }
}
