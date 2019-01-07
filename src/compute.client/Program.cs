using System;
using System.Text;

namespace computegen
{
    class Program
    {

        static void Main(string[] args)
        {
            const string rhinocommonPath = @"C:\dev\github\mcneel\rhino\src4\DotNetSDK\rhinocommon\dotnet";
            Console.WriteLine("[BEGIN PARSE]");
            Console.ForegroundColor = ConsoleColor.DarkGreen;
            ClassBuilder.BuildClassDictionary(rhinocommonPath);
            Console.ResetColor();
            Console.WriteLine("[END PARSE]");

            // just do a small number of classes to get started
            string[] filter = new string[] {
                ".Mesh", ".Brep", ".Curve", ".BezierCurve", ".Extrusion", ".NurbsCurve", ".Intersection"
            };

            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine("Writing javascript client");
            var js = new JavascriptClient();
            js.Write(ClassBuilder.AllClasses, "compute.rhino3d.js", filter);
            Console.WriteLine("Writing python client");
            var py = new PythonClient();
            py.Write(ClassBuilder.AllClasses, "", filter);
            Console.WriteLine("Writing C# client");
            var cs = new DotNetClient();
            cs.Write(ClassBuilder.AllClasses, "RhinoCompute.cs", filter);

            StringBuilder sb = new StringBuilder();
            foreach(var cl in ClassBuilder.AllClasses)
            {
                if (cl.Value.Methods.Count == 0)
                    continue;
                sb.AppendLine(cl.Key);
                foreach (var m in cl.Value.Methods)
                    sb.AppendLine($"  {m.Item1.Identifier}");
            }
            System.IO.File.WriteAllText("compute.txt", sb.ToString());
            sb = new StringBuilder();
            foreach (var cl in ClassBuilder.AllClasses)
            {
                if (cl.Value.NonComputeMethods.Count == 0)
                    continue;
                sb.AppendLine(cl.Key);
                foreach (var m in cl.Value.NonComputeMethods)
                    sb.AppendLine($"  {m.Item1.Identifier}");
            }
            System.IO.File.WriteAllText("rhino3dm.txt", sb.ToString());
        }

    }
}
