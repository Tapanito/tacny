﻿using System;
using System.Collections.Generic;
using Microsoft.Dafny;
using Dafny = Microsoft.Dafny;
using System.Diagnostics.Contracts;
using System.Threading;
using System.Threading.Tasks;
using Tacny;

namespace LazyTacny
{
    public class Interpreter
    {
        private Tacny.Program tacnyProgram;
        private SolutionList solution_list = new SolutionList();

        public Interpreter(Tacny.Program tacnyProgram)
        {
            Contract.Requires(tacnyProgram != null);
            this.tacnyProgram = tacnyProgram;
            this.solution_list = new SolutionList();
            //Console.SetOut(System.IO.TextWriter.Null);
        }

        public Dafny.Program ResolveProgram()
        {
            if (tacnyProgram.tactics.Count < 1)
            {
                return tacnyProgram.ParseProgram();
            }
            foreach (var member in tacnyProgram.members)
            {
                var res = LazyScanMemberBody(member.Value);
                if (res != null)
                {
                    solution_list.Add(res);
                    solution_list.Fin();
                }
            }

            // temp hack
            List<Solution> final = new List<Solution>();
            foreach (var solution in solution_list.GetFinal())
                final.Add(solution[0]);

            Dafny.Program prog = tacnyProgram.ParseProgram();
            foreach (var solution in final)
                solution.GenerateProgram(ref prog);
            tacnyProgram.dafnyProgram = prog;
            return prog;
                
        }

       
        private Solution LazyScanMemberBody(MemberDecl md)
        {

            Contract.Requires(md != null);
            Method m = md as Method;
            if (m == null)
                return null;
            if (m.Body == null)
                return null;

            List<IVariable> variables = new List<IVariable>();
            variables.AddRange(m.Ins);
            variables.AddRange(m.Outs);
            SolutionList sol_list = new SolutionList();
            sol_list.AddRange(solution_list.plist);

            foreach (var st in m.Body.Body)
            {
                // register local variables
                VarDeclStmt vds = st as VarDeclStmt;
                if (vds != null)
                    variables.AddRange(vds.Locals);

                UpdateStmt us = st as UpdateStmt;
                if (us != null)
                {
                    if (tacnyProgram.IsTacticCall(us))
                    {
                        try
                        {
                            tacnyProgram.SetCurrent(tacnyProgram.GetTactic(us), md);
                            // get the resolved variables
                            List<IVariable> resolved = tacnyProgram.GetResolvedVariables(md);
                            resolved.AddRange(m.Ins); // add input arguments as resolved variables
                            LazyTacny.Solution result = LazyTacny.Atomic.ResolveTactic(tacnyProgram.GetTactic(us), us, md, tacnyProgram, variables, resolved, sol_list);
                            tacnyProgram.PrintDebugData(tacnyProgram.currentDebug);
                            return result;
                        }
                        catch (Exception e)
                        {
                            Util.Printer.Error(e.Message);
                            return null;
                        }
                    }
                }
            }
            return null;
        }
    }
}
