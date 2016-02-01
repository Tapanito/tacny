﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Dafny;
using Dafny = Microsoft.Dafny;
using System.Diagnostics.Contracts;

namespace Tacny
{
    class TryCatchAtomic : Atomic, IAtomicStmt
    {

        public TryCatchAtomic(Atomic atomic) : base(atomic) { }

        public void Resolve(Statement st, ref List<Solution> solution_list)
        {
            TryCatch(st, ref solution_list);
        }


        private void TryCatch(Statement st, ref List<Solution> solution_list)
        {
            TacnyTryCatchBlockStmt stmt = st as TacnyTryCatchBlockStmt;
            List<Solution> result = null;
            ResolveBody(stmt.Body, out result);
            foreach (var sol in result)
            {
                //Atomic ac = this.Copy();
                Dafny.Program dprog = program.ParseProgram();
                sol.GenerateProgram(ref dprog);
                program.ClearBody(localContext.md);
                //program.MaybePrintProgram(dprog, null);
                program.ResolveProgram();
                // skip the solution if resolution failed    

                //program.MaybePrintProgram(dprog, null);
                program.VerifyProgram();
                if (!program.HasError())
                {
                    // change back the context of the state
                    sol.state.localContext.tac_body = localContext.tac.Body.Body;// sol.state.localContext.tac.Body.Body;
                    sol.state.localContext.tac = localContext.tac;
                    sol.state.localContext.SetCounter(localContext.GetCounter());
                    solution_list.Add(sol);
                    return;
                }
            }
            //'if we got here, that means that each solution failed the try block, evaluate the catch block
            if (stmt.Ctch != null)
            {
                result = null;
                ResolveBody(stmt.Ctch, out result);
                foreach (var sol in result)
                {
                    // change back the context of the state
                    sol.state.localContext.tac_body = localContext.tac.Body.Body;// sol.state.localContext.tac.Body.Body;
                    sol.state.localContext.tac = localContext.tac;
                    sol.state.localContext.SetCounter(localContext.GetCounter());
                    solution_list.Add(sol);
                }
            }
        }
    }
}