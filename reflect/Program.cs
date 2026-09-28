using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.IO;

// Metadata-only read of acdbmgd.dll: what can we actually call on a table Cell?
// Reflection-only load avoids needing AutoCAD present.
class Program
{
    static void Main()
    {
        string dll = @"C:\Program Files\Autodesk\AutoCAD LT 2026\acdbmgd.dll";
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();

        string[] want = { "Cell", "CellContent", "CellContentCollection",
                          "CellRange", "Table", "CellOption", "CellType" };

        foreach (var h in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(h);
            string ns = md.GetString(td.Namespace);
            string name = md.GetString(td.Name);
            if (!ns.StartsWith("Autodesk.AutoCAD.DatabaseServices")) continue;
            if (!want.Contains(name)) continue;

            Console.WriteLine($"\n=== {ns}.{name} ===");

            var props = td.GetProperties()
                .Select(ph => md.GetString(md.GetPropertyDefinition(ph).Name))
                .OrderBy(s => s).ToList();
            Console.WriteLine("  properties: " + string.Join(", ", props));

            var methods = td.GetMethods()
                .Select(mh => md.GetString(md.GetMethodDefinition(mh).Name))
                .Where(s => !s.StartsWith("get_") && !s.StartsWith("set_")
                            && !s.StartsWith(".") && !s.StartsWith("op_"))
                .Distinct().OrderBy(s => s).ToList();
            Console.WriteLine("  methods   : " + string.Join(", ", methods));

            // Which properties have setters?
            var setters = td.GetProperties()
                .Select(ph => md.GetPropertyDefinition(ph))
                .Where(pd => !pd.GetAccessors().Setter.IsNil)
                .Select(pd => md.GetString(pd.Name))
                .OrderBy(s => s).ToList();
            Console.WriteLine("  settable  : " + string.Join(", ", setters));
        }
    }
}
