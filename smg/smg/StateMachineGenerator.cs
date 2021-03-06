﻿using System;
using System.Linq;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using smg.ArgumentParsing;
using smg.StateGeneration;

namespace smg
{
    /// <summary>
    /// Generates state wrappers for a stateful type.
    /// </summary>
    class StateMachineGenerator
    {
        /// <summary>
        /// Contains the help message to be presented.
        /// </summary>
        private static readonly string HELP = "Made by Tamir Vered 2016" + Environment.NewLine
            + Environment.NewLine
            + "This utility allows generation of safe state machine wrappers according to attributed compiled class with many possible usages such as: " + Environment.NewLine
            + " - Safe factories." + Environment.NewLine
            + " - Unit testing tools." + Environment.NewLine
            + " - Safe stateful object handling." + Environment.NewLine
            + Environment.NewLine
            + "Usage:" + Environment.NewLine
            + "smg.exe source_assembly source_type_full_name destination_folder [/A | /D]" + Environment.NewLine
            + Environment.NewLine
            + "  /A | /D     Set output to compiled DLL instead of source files." + Environment.NewLine
            + Environment.NewLine
            + "For more information visit the project's github on https://github.com/TamirVered/StateMachineGenerator" + Environment.NewLine;

        /// <summary>
        /// Used as entry point.
        /// </summary>
        /// <param name="args">Arguments which will be used for the application to determine how to work (see: <see cref="HELP"/>).</param>
        static void Main(string[] args)
        {
            ArgsData argsData;

            if (!ArgsParser.TryParse(args, out argsData))
            {
                DisplayHelp();
                return;
            }

            if (argsData.Help)
            {
                DisplayHelp();
                return;
            }

            Assembly assembly = SafelyGetAssembly(argsData.SourceAssemblyPath);
            if (assembly == null)
            {
                return;
            }

            Type type = SafelyGetType(assembly, argsData.StateClassName);
            if (type == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(argsData.OutputPath) ||
                !Directory.Exists(argsData.OutputPath))
            {
                Console.WriteLine("Output directory is invalid or does not exist. use /h for help.");
                DisplayHelp();
                return;
            }

            StateGenerator generator = StateGenerator.GetFromType(type);

            CodeDomProvider codeProvider = CodeDomProvider.CreateProvider("CSharp");

            if (!argsData.GenerateAssembly)
            {
                GenerateSource(generator, codeProvider, argsData);
            }
            else
            {
                GenerateAssembly(argsData, type.Name + "StateWrappers.dll", codeProvider, generator);
            }
        }

        /// <summary>
        /// Outputs the generated wrapper classes to assembly file.
        /// </summary>
        /// <param name="argsData">Contains the arguments that were used to run the application.</param>
        /// <param name="outputName">The name and path to the output assembly file.</param>
        /// <param name="codeProvider">A <see cref="CodeDomProvider"/> initialized with the desired language to generate the code.</param>
        /// <param name="generator">The <see cref="StateGenerator"/> which generates the wrapper classes.</param>
        private static void GenerateAssembly(ArgsData argsData, string outputName, CodeDomProvider codeProvider,
            StateGenerator generator)
        {
            CompilerParameters compilerParameters = new CompilerParameters
            {
                GenerateInMemory = false,
                OutputAssembly = Path.Combine(argsData.OutputPath, outputName),
                ReferencedAssemblies = {argsData.SourceAssemblyPath},
            };
            CompilerResults results = codeProvider.CompileAssemblyFromDom(compilerParameters, generator.GetCompileUnit());
        }

        /// <summary>
        /// Outputs the generated wrapper classes to source files.
        /// </summary>
        /// <param name="generator">The <see cref="StateGenerator"/> which generates the wrapper classes.</param>
        /// <param name="codeProvider">A <see cref="CodeDomProvider"/> initialized with the desired language to generate the code.</param>
        /// <param name="argsData">Contains the arguments that were used to run the application.</param>
        private static void GenerateSource(StateGenerator generator, CodeDomProvider codeProvider, ArgsData argsData)
        {
            CodeGeneratorOptions codeGeneratorOptions = new CodeGeneratorOptions
            {
                BlankLinesBetweenMembers = true,
                IndentString = "\t",
                BracingStyle = "C",
            };

            foreach (CodeNamespace codeNamespace in generator.GetCompileUnit().Namespaces)
            {
                foreach (CodeTypeDeclaration codeTypeDeclaration in codeNamespace.Types)
                {
                    CodeCompileUnit compileUnit = new CodeCompileUnit();
                    CodeNamespace newNamespace = new CodeNamespace(codeNamespace.Name);
                    newNamespace.Types.Add(codeTypeDeclaration);
                    compileUnit.Namespaces.Add(newNamespace);

                    StringBuilder output = new StringBuilder();
                    using (TextWriter writer = new StringWriter(output))
                    {
                        codeProvider.GenerateCodeFromCompileUnit(compileUnit, writer, codeGeneratorOptions);
                    }

                    string outputString = output.ToString();
                    if (codeGeneratorOptions.BlankLinesBetweenMembers)
                    {
                        outputString = FixBlankLines(outputString);
                    }

                    File.WriteAllText(Path.Combine(argsData.OutputPath, codeTypeDeclaration.Name + "." + codeProvider.FileExtension),
                        outputString);
                }
            }
        }

        /// <summary>
        /// Fixes a problem with <see cref="CodeGeneratorOptions.BlankLinesBetweenMembers"/> which adds blank line at the beginning of a class and doesn't add them in regions.
        /// </summary>
        /// <param name="code">The code of a class to be fixed.</param>
        /// <returns>The input <paramref name="code"/> after fixing its code generation failiurs.</returns>
        private static string FixBlankLines(string code)
        {
            string outputFixStartRegion = Regex.Replace(code, $"#region.*{Environment.NewLine}",
                "$&" + Environment.NewLine);

            string outputFixEndRegion = Regex.Replace(outputFixStartRegion, ".*endregion.*",
                Environment.NewLine + "$&");

            Regex namespaceScopeNewLine = new Regex("{" + Environment.NewLine + "." + Environment.NewLine);

            outputFixEndRegion = namespaceScopeNewLine.Replace(outputFixEndRegion, "{", 1);

            Regex classScopeNewLine = new Regex("{" + Environment.NewLine);

            return classScopeNewLine.Replace(outputFixEndRegion, "{", 1);
        }

        /// <summary>
        /// Gets a <see cref="Type"/> from a given <see cref="Assembly"/>, returns null and print message if fails.
        /// </summary>
        /// <param name="assembly">The <see cref="Assembly"/> from which the <see cref="Type"/> will be retrieved.</param>
        /// <param name="typeName">The full name of the <see cref="Type"/> to be retrieved.</param>
        /// <returns>A <see cref="Type"/> retrieved from the provided <see cref="Assembly"/> (<c>null</c> if failed).</returns>
        private static Type SafelyGetType(Assembly assembly, string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                Console.WriteLine("Source type name is invalid. use /h for help.");
                DisplayHelp();
                return null;
            }
            Type type;
            try
            {
                type = assembly.GetType(typeName);
            }
            catch (Exception exception)
            {
                Console.WriteLine("Could not load type from the given assembly. use /h for help."
                                  + Environment.NewLine + "Inner error message: " + Environment.NewLine + exception.Message);
                DisplayHelp();
                return null;
            }
            if (type == null)
            {
                Console.WriteLine($"No type with full name '{typeName}' was found in the given assembly.");
                DisplayHelp();
                return null;
            }
            return type;
        }

        /// <summary>
        /// Loads an <see cref="Assembly"/> from a given path, returns null and print message if fails.
        /// </summary>
        /// <param name="path">The path from which the <see cref="Assembly"/> will be loaded.</param>
        /// <returns>An <see cref="Assembly"/> loaded from the provided path (<c>null</c> if failed).</returns>
        private static Assembly SafelyGetAssembly(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !File.Exists(path))
            {
                Console.WriteLine("Source assembly path is invalid. use /h for help.");
                DisplayHelp();
                return null;
            }
            Assembly assembly;
            try
            {
                AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += ReflectionOnlyAssemblyResolve;
                assembly = Assembly.ReflectionOnlyLoadFrom(path);
            }
            catch (Exception exception)
            {
                Console.WriteLine("Could not load assembly from the given path. use /h for help."
                                  + Environment.NewLine + "Inner error message: " + Environment.NewLine +
                                  exception.Message);
                DisplayHelp();
                return null;
            }
            finally
            {
                AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve -= ReflectionOnlyAssemblyResolve;
            }
            return assembly;
        }

        private static Assembly ReflectionOnlyAssemblyResolve(object sender, ResolveEventArgs args)
        {
            return Assembly.ReflectionOnlyLoad(args.Name);
        }

        private static void DisplayHelp()
        {
            Console.WriteLine(HELP);
        }
    }
}
