using dnlib.DotNet;

namespace PCLPatcher;

internal enum PatchState { NeedsPatch, Applied, Unsupported }

// Inspect is read-only. Captured edits execute only after every rule passes.
internal sealed record PatchAnalysis(string Id, string Description, PatchState State, string Detail,
    MethodDef Method = null, Action Edit = null);

internal abstract class PatchRule(string id, string description)
{
    public string Id { get; } = id;
    public string Description { get; } = description;
    public PatchAnalysis Inspect(ModuleDef module)
    {
        try
        {
            var analysis = Analyze(module);
            Il.Validate(analysis.Method);
            return analysis;
        }
        catch (UnsupportedException ex) { return new(Id, Description, PatchState.Unsupported, ex.Message); }
        catch (VerificationException ex) { return new(Id, Description, PatchState.Unsupported, ex.Message); }
    }
    protected abstract PatchAnalysis Analyze(ModuleDef module);
    protected PatchAnalysis Applied(MethodDef method) => new(Id, Description, PatchState.Applied, method.FullName, method);
    protected PatchAnalysis Change(MethodDef method, Action edit) => new(Id, Description, PatchState.NeedsPatch, method.FullName, method, edit);
}
