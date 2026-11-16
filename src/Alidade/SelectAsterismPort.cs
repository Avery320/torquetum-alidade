using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Grasshopper.Kernel;
using GH_IO.Serialization;

namespace Torquetum.Alidade;

public sealed class SelectAsterismPort : AlidadeAsyncComponent
{
    private sealed record Binding(string ProjectId, string PortId, string Direction);

    private ProjectSession? _project;
    private IReadOnlyList<GeometryPort>? _items;
    private Binding? _binding;
    private bool HasPortId => Params.Input[1].SourceCount != 0 || Params.Input[1].VolatileDataCount != 0;

    public SelectAsterismPort()
        : base("Select Asterism Port", "Select Port", "Select an Asterism input or output from a Torquetum project.", "Session") { }

    public override Guid ComponentGuid => new("8006ed19-e281-404e-9ca2-acd6992c6223");
    protected override System.Drawing.Bitmap Icon => ComponentIcons.Load("SelectPort");

    protected override void RegisterInputParams(GH_InputParamManager manager)
    {
        manager.AddParameter(new AlidadeProjectParameter());
        manager.AddTextParameter("Port ID", "ID", "Optional Asterism port ID. When omitted, select a port from the component menu.",
            GH_ParamAccess.item);
        manager[0].Optional = manager[1].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager manager)
    {
        manager.AddParameter(new AlidadePortReferenceParameter());
        manager.AddTextParameter("Status", "S", "Selection and readiness.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        var document = OnPingDocument();
        var project = ConnectedProject(data, document);
        if (!ReferenceEquals(project, _project))
        {
            _project = project;
            LoadList();
        }

        var byId = HasPortId;
        var binding = byId ? null : _binding;
        var portId = byId ? SingleInput<string>(data, 1) : binding?.PortId;
        var error = Failure?.Message;

        string status;
        if (project == null) status = "Connect Project";
        else if (binding != null && binding.ProjectId != project.Status.ProjectId)
            status = error = "This port belongs to another project. Pair again or choose a new port.";
        else if (IsPending) status = "Loading ports…";
        else if (error != null) status = error;
        else if (_items == null) status = "Refresh port list";
        else if (byId && string.IsNullOrWhiteSpace(portId)) status = "Connect one Port ID";
        else if (portId == null) status = _items.Count == 0 ? "No exchange ports" : "Choose a port";
        else
        {
            var item = _items.FirstOrDefault(candidate => candidate.Id == portId
                && (binding == null || candidate.Direction == binding.Direction));
            if (item == null) status = error = AlidadeClient.ErrorMessage("PORT_NOT_FOUND");
            else
            {
                var target = item.Reference(project.Status);
                data.SetData(0, new AlidadePortReference(target));
                status = $"{target.DisplayName} ({target.Direction})";
            }
        }
        ReportStatus(data, 1, status, error);
    }

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        var document = OnPingDocument();
        var enabled = !IsPending && CanUseProject(document, _project);
        Menu_AppendItem(menu, "Refresh list", (_, _) => LoadList(), enabled);
        if (!enabled || _items == null || _project is not { } project || HasPortId)
            return;

        var list = new ToolStripMenuItem("Asterism ports");
        menu.Items.Add(list);
        foreach (var item in _items)
        {
            var binding = new Binding(project.Status.ProjectId, item.Id, item.Direction);
            var name = GeometryPortReference.Display(item.Name, item.Id);
            Menu_AppendItem(list.DropDown, $"{name} ({item.Direction})", (_, _) => Select(project, binding), true, _binding == binding);
        }
    }

    private void Select(ProjectSession project, Binding binding)
    {
        var document = OnPingDocument();
        if (_binding == binding || !ReferenceEquals(_project, project) || !CanUseProject(document, project))
            return;
        RecordUndoEvent("Select Asterism port");
        _binding = binding;
        RequestSolution(document);
    }

    private void LoadList()
    {
        var document = OnPingDocument();
        var project = _project;
        _items = null;
        if (CanUseProject(document, project))
            StartRequest(document, project, cancellation => AlidadeClient.ListPortsAsync(project, document.RuntimeID, cancellation),
                items => _items = items);
        else CancelRequest();
    }

    protected override void ResetState()
    {
        _project = null;
        _items = null;
    }

    public override bool Write(GH_IWriter writer)
    {
        if (_binding is { } binding)
        {
            writer.SetString("AlidadeProjectId", binding.ProjectId);
            writer.SetString("AlidadePortId", binding.PortId);
            writer.SetString("AlidadePortDirection", binding.Direction);
        }
        return base.Write(writer);
    }

    public override bool Read(GH_IReader reader)
    {
        var result = base.Read(reader);
        _binding = reader.ItemExists("AlidadeProjectId") && reader.ItemExists("AlidadePortId")
            && reader.ItemExists("AlidadePortDirection")
            && reader.GetString("AlidadeProjectId") is { Length: > 0 } project
            && reader.GetString("AlidadePortId") is { Length: > 0 } port
            && reader.GetString("AlidadePortDirection") is { } direction && direction is ("input" or "output")
            ? new Binding(project, port, direction) : null;
        return result;
    }
}
