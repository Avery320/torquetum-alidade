using System;
using Grasshopper.Kernel;

namespace Torquetum.Alidade;

public sealed class AlidadeAssemblyInfo : GH_AssemblyInfo
{
    public override Guid Id => new("f0bf8097-ecaf-4858-b36d-f09790ae7cf5");

    public override string Name => "Alidade";

    public override string Description => "Geometry and data tree exchange with Torquetum.";

    public override string AuthorName => "AveryTsai";
}
