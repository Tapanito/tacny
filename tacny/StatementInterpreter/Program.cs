﻿using System;
using System.Collections.Generic;
using System.IO;
using System.CodeDom.Compiler;
using Microsoft.Dafny;
using Dafny = Microsoft.Dafny;
using Microsoft.Boogie;
using Bpl = Microsoft.Boogie;
using System.Diagnostics.Contracts;
using System.Linq;
namespace Tacny
{
    public class Program
    {
        const bool DEBUG = true;
        private IList<string> fileNames;
        private string _programId;
        public string programId
        {
            set
            { _programId = value; }
            get { return _programId; }
        }
        private readonly Dafny.Program _original;
        private Dafny.Program _program;
        public Dafny.Program dafnyProgram
        {
            set
            {
                resolved = false;
                _program = value;
                errorInfo = null;
                stats = null;
            }
            get
            {
                return _program;
            }

        }
        public Bpl.ErrorInformation errorInfo;
        public List<Bpl.ErrorInformation> errList;
        public PipelineOutcome po;
        public Bpl.PipelineStatistics stats;
        public bool resolved = false;

        public readonly Dictionary<string, Tactic> tactics;
        public readonly Dictionary<string, MemberDecl> members;
        public readonly List<DatatypeDecl> globals;
        private Util.Printer printer;
        public DebugData currentDebug;
        private List<DebugData> debugDataList;
        public class DebugData
        {
            public string tactic = null;
            public string method = null;
            public int BadBranchCount = 0;      // number of branches where resolution failed
            public int GoodBranchCount = 0;     // number of branches where resolution succeeded
            public int VerificationFailure = 0; // number of times verification failed
            public int VerificationSucc = 0;    // number of times verificaiton succeeded
            public int TotalBranchCount = 0;    // total number of branches
            public int CallsToBoogie = 0;       // number of calls made to Boogie during tactic resolution
            public int CallsToDafny = 0;        // number of calls to Dafny resolver
            public int StartTime = 0;           // Unix timestamp when the tactic resolution begins
            public int EndTime = 0;             // Unix timestamp when the tactic resolution finishes

            public DebugData(string tactic, string method)
            {
                StartTime = (Int32)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;
                this.tactic = tactic;
                this.method = method;
            }

            private void Fin()
            {
                if (EndTime == 0)
                    EndTime = (Int32)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;
            }
            public void PrintDebugData(Program program)
            {
                Fin();
                TextWriter tw = new System.IO.StreamWriter(program.fileNames[0] + "_debug.dat", true);
                program.PrintDebugMessage("Method: {0}", tw, method);
                program.PrintDebugMessage("Tactic: {0}", tw, tactic);
                program.PrintDebugMessage("Execution time: {0} seconds", tw, EndTime - StartTime);
                program.PrintDebugMessage("Generated branches: {0}", tw, TotalBranchCount);
                program.PrintDebugMessage("Generated invalid branches: {0}", tw, BadBranchCount);
                program.PrintDebugMessage("Generated valid branches: {0}", tw, GoodBranchCount);
                program.PrintDebugMessage("Verification failed {0} times", tw, VerificationFailure);
                program.PrintDebugMessage("Verification succeeded {0} times", tw, VerificationSucc);
                program.PrintDebugMessage("Times Boogie was called: {0}", tw, CallsToBoogie);
                program.PrintDebugMessage("Times Dafny was called: {0}", tw, CallsToDafny);
            }

            public void PrintCsvDebugData(Program program)
            {
                Fin();
                TextWriter tw = new System.IO.StreamWriter(program.fileNames[0] + "_debug.csv");
                program.PrintDebugMessage("tacitic, method, exec_time, branch_count, inv_branch_count, vld_branch_count, verif_fail, verif_succ, boogie_calls, dafny_calls\n", tw);
                program.PrintDebugMessage("{0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}", tw,
                    tactic, method, 
                    EndTime - StartTime, TotalBranchCount,
                    BadBranchCount, GoodBranchCount,
                    VerificationFailure, VerificationSucc,
                    CallsToBoogie, CallsToDafny);
            }
        }


        public void PrintDebugData(DebugData debugData, bool isCsv = false)
        {
            if (isCsv)
                debugData.PrintCsvDebugData(this);
            else
                debugData.PrintDebugData(this);
        }


        private void IncBadBranchCount(DebugData debugData)
        {
            debugData.BadBranchCount++;
        }

        private void IncGoodBranchCount(DebugData debugData)
        {
            debugData.GoodBranchCount++;
        }

        public void IncTotalBranchCount(DebugData debugData)
        {
            debugData.TotalBranchCount++;
        }

        private void IncVerificationFailure(DebugData debugData)
        {
            debugData.VerificationFailure++;
        }

        private void IncVerificationSuccess(DebugData debugData)
        {
            debugData.VerificationSucc++;
        }

        private void IncCallsToBoogie(DebugData debugData)
        {
            debugData.CallsToBoogie++;
        }

        private void IncCallsToDafny(DebugData debugData)
        {
            debugData.CallsToDafny++;
        }

        public void SetCurrent(Tactic tac, MemberDecl md)
        {
            DebugData dd = debugDataList.Where(i => i.tactic == tac.Name && i.method == md.Name).LastOrDefault();
            if (dd == null)
            {
                dd = new DebugData(tac.Name, md.Name);
                debugDataList.Add(dd);
            }
            currentDebug = dd;
        }

        public Program(IList<string> fileNames, string programId, string programName = null)
        {
            this.fileNames = fileNames;
            this.programId = programId;
            string err = ParseCheck(fileNames, programId, out _original);
            dafnyProgram = ParseProgram();
            
            
            if (err != null)
                throw new ArgumentException(err);
            Init(out tactics, out members, out globals);
        }

        private void Init(out Dictionary<string, Tactic> tactics, out Dictionary<string, MemberDecl> members, out List<DatatypeDecl> globals)
        {
            tactics = new Dictionary<string, Tactic>();
            members = new Dictionary<string, MemberDecl>();
            globals = new List<DatatypeDecl>();
            debugDataList = new List<DebugData>();
            foreach (var item in dafnyProgram.DefaultModuleDef.TopLevelDecls)
            {
                ClassDecl curDecl = item as ClassDecl;
                if (curDecl != null)
                {
                    // scan each member for tactic calls and resolve if found
                    foreach (var member in curDecl.Members)
                    {
                        Tactic tac = member as Tactic;
                        if (tac != null)
                            tactics.Add(tac.Name, tac);
                        else
                            members.Add(member.Name, member);
                    }
                }
                else
                {
                    DatatypeDecl dd = item as DatatypeDecl;
                    if (dd != null)
                        globals.Add(dd);
                }
            }
        }

        /// <summary>
        /// Create new tacny Program instance
        /// </summary>
        /// <returns></returns>
        public Program NewProgram()
        {
            return new Program(fileNames, programId);

        }

        /// <summary>
        /// Create new instance of the working Dafny.Program
        /// </summary>
        /// <returns>Instance of dafny.Program</returns>
        public Dafny.Program ParseProgram()
        {
            //Dafny.Program prog;
            //ParseCheck(fileNames, programId, out prog);
            Cloner cl = new Cloner();
            ModuleDecl module = new Dafny.LiteralModuleDecl(cl.CloneModuleDefinition(_original.DefaultModuleDef, _original.Name), null);

            this.dafnyProgram = new Dafny.Program(_original.Name, module, _original.BuiltIns);
            return dafnyProgram;
        }

        public void VerifyProgram()
        {
            if (!resolved)
                ResolveProgram();
            VerifyProgram(dafnyProgram);
        }

        public void VerifyProgram(Dafny.Program prog)
        {
            Bpl.Program boogieProgram;
            IncCallsToBoogie(currentDebug);
            Translate(prog, fileNames, programId, out boogieProgram);
            po = BoogiePipeline(boogieProgram, prog, fileNames, programId);
            if (stats.ErrorCount == 0)
                IncVerificationSuccess(currentDebug);
            else
                IncVerificationFailure(currentDebug);

        }

        public bool ResolveProgram()
        {
            if (ResolveProgram(dafnyProgram) == 0)
                resolved = true;
            return resolved;
        }

        public int ResolveProgram(Dafny.Program program)
        {
            IncCallsToDafny(currentDebug);
            Dafny.Resolver r = new Dafny.Resolver(program);
            r.ResolveProgram(program);
            if (r.ErrorCount != 0)
            {
                Util.Printer.Error("{0} resolution/type errors detected in {1}", r.ErrorCount, program.Name);
                IncBadBranchCount(currentDebug);
            }
            else
            {
                IncGoodBranchCount(currentDebug);
            }
            return r.ErrorCount;
        }

        public static MemberDecl FindMember(Dafny.Program program, string name)
        {
            foreach (var item in program.DefaultModuleDef.TopLevelDecls)
            {
                ClassDecl cd = item as ClassDecl;
                if (cd != null)
                    foreach (var member in cd.Members)
                        if (member.Name == name)
                            return member;
            }

            return null;
        }

        public List<DatatypeDecl> GetGlobals(Dafny.Program prog)
        {
            List<DatatypeDecl> data = new List<DatatypeDecl>();
            foreach (TopLevelDecl d in prog.DefaultModuleDef.TopLevelDecls)
            {
                DatatypeDecl dd = d as DatatypeDecl;
                if (dd != null)
                    data.Add(dd);
            }

            return data;
        }

        public Token GetErrorToken()
        {
            if (errorInfo != null)
                return (Token)errorInfo.Tok;

            return null;
        }

        public bool HasError()
        {
            if (stats != null)
                return stats.ErrorCount > 0;

            return true;
        }

        public void ClearBody(MemberDecl md)
        {
            ClearBody(md, dafnyProgram);
        }

        /// <summary>
        /// Remove unresolved tactic calls from the program
        /// </summary>
        /// <param name="program">Dafny Program</param>
        public void ClearBody(MemberDecl md, Dafny.Program program)
        {
            foreach (var item in program.DefaultModuleDef.TopLevelDecls)
            {
                ClassDecl cd = item as ClassDecl;
                if (cd != null)
                {
                    foreach (var member in cd.Members)
                    {
                        Method m = member as Method;
                        if (m != null && m.Name != md.Name)
                        {
                            m.Body = null;
                        }
                    }
                }
            }
        }

        public List<IVariable> GetResolvedVariables(MemberDecl md)
        {
            ParseProgram();
            ClearBody(md);
            ResolveProgram();
            List<IVariable> result = null;
            foreach (var item in dafnyProgram.DefaultModuleDef.TopLevelDecls)
            {
                ClassDecl cd = item as ClassDecl;
                if (cd != null)
                {
                    foreach (var member in cd.Members)
                    {
                        Method m = member as Method;
                        if (m == null)
                            continue;
                        if (m.Name == md.Name)
                        {
                            result = new List<IVariable>();
                            foreach (var stmt in m.Body.Body)
                            {
                                VarDeclStmt vds = stmt as VarDeclStmt;
                                if (vds != null)
                                {
                                    foreach(var local in vds.Locals)
                                    {
                                        if (local.Type != null)
                                            result.Add(local);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            return result;
        }
        
        private string GetSignature(UpdateStmt us)
        {
            ExprRhs er = us.Rhss[0] as ExprRhs;
            if (er == null)
                return null;
            ApplySuffix asx = er.Expr as ApplySuffix;
            if (asx == null)
                return null;
            return asx.Lhs.tok.val;
        }

        public bool IsTacticCall(UpdateStmt us)
        {
            string name = GetSignature(us);
            if (name == null)
                return false;
            return tactics.ContainsKey(name);
        }

        public Tactic GetTactic(string name)
        {
            if (!tactics.ContainsKey(name))
                return null;

            return tactics[name];
        }

        public Tactic GetTactic(UpdateStmt us)
        {
            string name = GetSignature(us);
            if (name == null)
                return null;

            return GetTactic(name);
        }

        public MemberDecl GetMember(string name)
        {
            Contract.Requires(name != null);
            if (!members.ContainsKey(name))
                return null;
            return members[name];
        }

        #region Parser
        /// <summary>
        /// Returns null on success, or an error string otherwise.
        /// </summary>
        public string ParseCheck(IList<string/*!*/>/*!*/ fileNames, string/*!*/ programName, out Dafny.Program program)
        //modifies Bpl.CommandLineOptions.Clo.XmlSink.*;
        {
            Contract.Requires(programName != null);
            Contract.Requires(fileNames != null);
            program = null;
            ModuleDecl module = new Dafny.LiteralModuleDecl(new Dafny.DefaultModuleDecl(), null);
            BuiltIns builtIns = new Dafny.BuiltIns();
            foreach (string dafnyFileName in fileNames)
            {
                Contract.Assert(dafnyFileName != null);
                if (Bpl.CommandLineOptions.Clo.XmlSink != null && Bpl.CommandLineOptions.Clo.XmlSink.IsOpen)
                {
                    Bpl.CommandLineOptions.Clo.XmlSink.WriteFileFragment(dafnyFileName);
                }
                if (Bpl.CommandLineOptions.Clo.Trace)
                {
                    Console.WriteLine("Parsing " + dafnyFileName);
                }

                string err = ParseFile(dafnyFileName, Bpl.Token.NoToken, module, builtIns, new Dafny.Errors());
                if (err != null)
                {
                    return err;
                }
            }

            if (!DafnyOptions.O.DisallowIncludes)
            {
                string errString = ParseIncludes(module, builtIns, fileNames, new Dafny.Errors());
                if (errString != null)
                {
                    return errString;
                }
            }

            program = new Dafny.Program(programName, module, builtIns);


            if (Bpl.CommandLineOptions.Clo.NoResolve || Bpl.CommandLineOptions.Clo.NoTypecheck) { return null; }

            return null;
        }
        // Lower-case file names before comparing them, since Windows uses case-insensitive file names
        private class IncludeComparer : IComparer<Include>
        {
            public int Compare(Include x, Include y)
            {
                return x.fullPath.ToLower().CompareTo(y.fullPath.ToLower());
            }
        }

        public static string ParseIncludes(ModuleDecl module, BuiltIns builtIns, IList<string> excludeFiles, Dafny.Errors errs)
        {
            SortedSet<Include> includes = new SortedSet<Include>(new IncludeComparer());
            foreach (string fileName in excludeFiles)
            {
                includes.Add(new Include(null, fileName, Path.GetFullPath(fileName)));
            }
            bool newlyIncluded;
            do
            {
                newlyIncluded = false;

                List<Include> newFilesToInclude = new List<Include>();
                foreach (Include include in ((LiteralModuleDecl)module).ModuleDef.Includes)
                {
                    bool isNew = includes.Add(include);
                    if (isNew)
                    {
                        newlyIncluded = true;
                        newFilesToInclude.Add(include);
                    }
                }

                foreach (Include include in newFilesToInclude)
                {
                    string ret = ParseFile(include.filename, include.tok, module, builtIns, errs, false);
                    if (ret != null)
                    {
                        return ret;
                    }
                }
            } while (newlyIncluded);

            return null; // Success
        }

        private static string ParseFile(string dafnyFileName, Bpl.IToken tok, ModuleDecl module, BuiltIns builtIns, Dafny.Errors errs, bool verifyThisFile = true)
        {
            var fn = DafnyOptions.Clo.UseBaseNameForFileName ? Path.GetFileName(dafnyFileName) : dafnyFileName;
            try
            {
                
                int errorCount = Dafny.Parser.Parse(dafnyFileName, module, builtIns, errs, verifyThisFile);
                if (errorCount != 0)
                {
                    return string.Format("{0} parse errors detected in {1}", errorCount, fn);
                }
            }
            catch (IOException e)
            {
                errs.SemErr(tok, "Unable to open included file");
                return string.Format("Error opening file \"{0}\": {1}", fn, e.Message);
            }
            return null; // Success
        }
        #endregion

        #region Boogie
        /// <summary>
        /// Translates Dafny program to Boogie program
        /// </summary>
        /// <returns>Exit value</returns>
        public static void Translate(Dafny.Program dafnyProgram, IList<string> fileNames, string programId, out Bpl.Program boogieProgram)
        {

            Dafny.Translator translator = new Dafny.Translator();
            boogieProgram = translator.Translate(dafnyProgram);
            if (CommandLineOptions.Clo.PrintFile != null)
            {
                ExecutionEngine.PrintBplFile(CommandLineOptions.Clo.PrintFile, boogieProgram, false, false, CommandLineOptions.Clo.PrettyPrint);
            }
        }

        /// <summary>
        /// Pipeline the boogie program to Dafny where it is valid
        /// </summary>
        /// <returns>Exit value</returns>
        public Bpl.PipelineOutcome BoogiePipeline(Bpl.Program boogieProgram, Dafny.Program dafnyProgram, IList<string> fileNames, string programIds)
        {

            string bplFilename;
            if (CommandLineOptions.Clo.PrintFile != null)
            {
                bplFilename = CommandLineOptions.Clo.PrintFile;
            }
            else
            {
                string baseName = cce.NonNull(Path.GetFileName(fileNames[fileNames.Count - 1]));
                baseName = cce.NonNull(Path.ChangeExtension(baseName, "bpl"));
                bplFilename = Path.Combine(Path.GetTempPath(), baseName);
            }


            Bpl.PipelineOutcome oc = BoogiePipelineWithRerun(boogieProgram, bplFilename, out stats, 1 < Dafny.DafnyOptions.Clo.VerifySnapshots ? programId : null);

            //var allOk = stats.ErrorCount == 0 && stats.InconclusiveCount == 0 && stats.TimeoutCount == 0 && stats.OutOfMemoryCount == 0;
            return oc;
        }

        /// <summary>
        /// Resolve, type check, infer invariants for, and verify the given Boogie program.
        /// The intention is that this Boogie program has been produced by translation from something
        /// else.  Hence, any resolution errors and type checking errors are due to errors in
        /// the translation.
        /// The method prints errors for resolution and type checking errors, but still returns
        /// their error code.
        /// </summary>
        public PipelineOutcome BoogiePipelineWithRerun(Bpl.Program/*!*/ program, string/*!*/ bplFileName,
            out PipelineStatistics stats, string programId)
        {
            Contract.Requires(program != null);
            Contract.Requires(bplFileName != null);
            Contract.Ensures(0 <= Contract.ValueAtReturn(out stats).InconclusiveCount && 0 <= Contract.ValueAtReturn(out stats).TimeoutCount);

            stats = new PipelineStatistics();
            LinearTypeChecker ltc;
            MoverTypeChecker mtc;
            PipelineOutcome oc = ExecutionEngine.ResolveAndTypecheck(program, bplFileName, out ltc, out mtc);
            switch (oc)
            {
                case PipelineOutcome.Done:
                    return oc;

                case PipelineOutcome.ResolutionError:
                case PipelineOutcome.TypeCheckingError:
                    {
                        ExecutionEngine.PrintBplFile(bplFileName, program, false, false, CommandLineOptions.Clo.PrettyPrint);
                        Console.WriteLine();
                        Console.WriteLine("*** Encountered internal translation error - re-running Boogie to get better debug information");
                        Console.WriteLine();

                        List<string/*!*/>/*!*/ fileNames = new List<string/*!*/>();
                        fileNames.Add(bplFileName);
                        Bpl.Program reparsedProgram = ExecutionEngine.ParseBoogieProgram(fileNames, true);
                        if (reparsedProgram != null)
                        {
                            ExecutionEngine.ResolveAndTypecheck(reparsedProgram, bplFileName, out ltc, out mtc);
                        }
                    }
                    return oc;

                case PipelineOutcome.ResolvedAndTypeChecked:
                    ExecutionEngine.EliminateDeadVariables(program);
                    ExecutionEngine.CollectModSets(program);
                    ExecutionEngine.CoalesceBlocks(program);
                    ExecutionEngine.Inline(program);
                    errList = new List<ErrorInformation>();
                    //return ExecutionEngine.InferAndVerify(program, stats, programId);
                    return ExecutionEngine.InferAndVerify(program, stats, programId, errorInfo =>
                    {
                        //errorInfo.BoogieErrorCode = null;
                        if (this.errorInfo == null)
                            this.errorInfo = errorInfo;
                        errList.Add(errorInfo);
                        //Console.WriteLine(errorInfo.FullMsg);
                        //errorListHolder.AddError(new DafnyError(errorInfo.Tok.filename, errorInfo.Tok.line - 1, errorInfo.Tok.col - 1, ErrorCategory.VerificationError, errorInfo.FullMsg, s, isRecycled, errorInfo.Model.ToString(), System.IO.Path.GetFullPath(_document.FilePath) == errorInfo.Tok.filename), errorInfo.ImplementationName, requestId);
                        //foreach (var aux in errorInfo.Aux)
                        //{
                        //  errorListHolder.AddError(new DafnyError(aux.Tok.filename, aux.Tok.line - 1, aux.Tok.col - 1, ErrorCategory.AuxInformation, aux.FullMsg, s, isRecycled, null, System.IO.Path.GetFullPath(_document.FilePath) == aux.Tok.filename), errorInfo.ImplementationName, requestId);
                        //}


                    });

                default:
                    Contract.Assert(false); throw new cce.UnreachableException();  // unexpected outcome
            }
        }

        #endregion

        #region Compilation
        public void Compile()
        {
            //printer.WriteTrailer(stats);
            if ((DafnyOptions.O.Compile /*&& allOk*/ && CommandLineOptions.Clo.ProcsToCheck == null) || DafnyOptions.O.ForceCompile)
                Dafny.DafnyDriver.CompileDafnyProgram(dafnyProgram, fileNames[0]);
        }

        private static void CompileDafnyProgram(Dafny.Program dafnyProgram, string dafnyProgramName, TextWriter outputWriter = null)
        {
            Contract.Requires(dafnyProgram != null);

            if (outputWriter == null)
            {
                outputWriter = Console.Out;
            }

            // Compile the Dafny program into a string that contains the C# program
            StringWriter sw = new StringWriter();
            Dafny.Compiler compiler = new Dafny.Compiler(sw);
            compiler.ErrorWriter = outputWriter;
            var hasMain = compiler.HasMain(dafnyProgram);
            if (DafnyOptions.O.RunAfterCompile && !hasMain)
            {
                // do no more
                return;
            }
            compiler.Compile(dafnyProgram);
            var csharpProgram = sw.ToString();
            bool completeProgram = compiler.ErrorCount == 0;

            // blurt out the code to a file
            if (DafnyOptions.O.SpillTargetCode)
            {
                string targetFilename = Path.ChangeExtension(dafnyProgramName, "cs");
                using (TextWriter target = new StreamWriter(new FileStream(targetFilename, System.IO.FileMode.Create)))
                {
                    target.Write(csharpProgram);
                    if (completeProgram)
                    {
                        outputWriter.WriteLine("Compiled program written to {0}", targetFilename);
                    }
                    else
                    {
                        outputWriter.WriteLine("File {0} contains the partially compiled program", targetFilename);
                    }
                }
            }

            // compile the program into an assembly
            if (!completeProgram)
            {
                // don't compile
            }
            else if (!CodeDomProvider.IsDefinedLanguage("CSharp"))
            {
                outputWriter.WriteLine("Error: cannot compile, because there is no provider configured for input language CSharp");
            }
            else
            {
                var provider = CodeDomProvider.CreateProvider("CSharp");
                var cp = new System.CodeDom.Compiler.CompilerParameters();
                cp.GenerateExecutable = hasMain;
                if (DafnyOptions.O.RunAfterCompile)
                {
                    cp.GenerateInMemory = true;
                }
                else if (hasMain)
                {
                    cp.OutputAssembly = Path.ChangeExtension(dafnyProgramName, "exe");
                    cp.GenerateInMemory = false;
                }
                else
                {
                    cp.OutputAssembly = Path.ChangeExtension(dafnyProgramName, "dll");
                    cp.GenerateInMemory = false;
                }
                cp.CompilerOptions = "/debug /nowarn:0164 /nowarn:0219";  // warning CS0164 complains about unreferenced labels, CS0219 is about unused variables
                cp.ReferencedAssemblies.Add("System.Numerics.dll");

                var cr = provider.CompileAssemblyFromSource(cp, csharpProgram);
                var assemblyName = Path.GetFileName(cr.PathToAssembly);
                if (DafnyOptions.O.RunAfterCompile && cr.Errors.Count == 0)
                {
                    outputWriter.WriteLine("Program compiled successfully");
                    outputWriter.WriteLine("Running...");
                    outputWriter.WriteLine();
                    var entry = cr.CompiledAssembly.EntryPoint;
                    try
                    {
                        object[] parameters = entry.GetParameters().Length == 0 ? new object[] { } : new object[] { new string[0] };
                        entry.Invoke(null, parameters);
                    }
                    catch (System.Reflection.TargetInvocationException e)
                    {
                        outputWriter.WriteLine("Error: Execution resulted in exception: {0}", e.Message);
                        outputWriter.WriteLine(e.InnerException.ToString());
                    }
                    catch (Exception e)
                    {
                        outputWriter.WriteLine("Error: Execution resulted in exception: {0}", e.Message);
                        outputWriter.WriteLine(e.ToString());
                    }
                }
                else if (cr.Errors.Count == 0)
                {
                    outputWriter.WriteLine("Compiled assembly into {0}", assemblyName);
                }
                else
                {
                    outputWriter.WriteLine("Errors compiling program into {0}", assemblyName);
                    foreach (var ce in cr.Errors)
                    {
                        outputWriter.WriteLine(ce.ToString());
                        outputWriter.WriteLine();
                    }
                }
            }
        }

        #endregion

        /// <summary>
        /// Print verified program to file
        /// </summary>
        public void Print()
        {
            foreach (var filename in fileNames)
            {
                printer = new Util.Printer(new System.IO.StreamWriter(filename.Substring(0, filename.LastIndexOf(".")) + ".tacny.dfy"), DafnyOptions.O.PrintMode);
                printer.PrintProgram(dafnyProgram);
            }
        }

        public void MaybePrintProgram(string filename)
        {
            MaybePrintProgram(dafnyProgram, filename);
        }
        /// <summary>
        /// Print the source code
        /// </summary>
        /// <param name="prog"></param>
        /// <param name="filename"></param>
        public void MaybePrintProgram(Dafny.Program prog, string filename)
        {
            // if program is not in debug mode disable console printing
            if (!DEBUG)
                return;
            TextWriter tw = null;
            if (filename == null || filename == "-")
            {
                tw = System.Console.Out;
                PrintProgram(tw, prog, DafnyOptions.O.PrintMode);
            }
            else
            {
                printer = new Util.Printer(new System.IO.StreamWriter(filename), DafnyOptions.O.PrintMode);
                printer.PrintProgram(dafnyProgram);
                
            }

        }

        private void PrintProgram(TextWriter tw, Dafny.Program prog, DafnyOptions.PrintModes printMode = DafnyOptions.PrintModes.Everything)
        {
            //if (printer == null)
            printer = new Util.Printer(tw, DafnyOptions.O.PrintMode);
            printer.PrintProgram(prog);
        }

        public void PrintDebugMessage(string message, TextWriter tw, params object[] args)
        {
            Util.Printer prt = new Util.Printer(tw, DafnyOptions.O.PrintMode);
            prt.PrintDebugMessage(message, fileNames[0], args);
        }
    }
}
