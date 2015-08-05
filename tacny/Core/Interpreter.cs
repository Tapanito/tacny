﻿using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Linq;
using Microsoft.Dafny;
using Dafny = Microsoft.Dafny;
using Microsoft.Boogie;
using Bpl = Microsoft.Boogie;
using System.Diagnostics.Contracts;


namespace Tacny
{

    public class ResolutionErrorReporter
    {
        public int ErrorCount = 0;

        public void Error(IToken tok, string msg, params object[] args)
        {
            Contract.Requires(tok != null);
            Contract.Requires(msg != null);
            ConsoleColor col = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;

            Console.WriteLine("{0}({1},{2}): Error: {3}",
                DafnyOptions.Clo.UseBaseNameForFileName ? System.IO.Path.GetFileName(tok.filename) : tok.filename, tok.line, tok.col - 1,
                string.Format(msg));
            Console.ForegroundColor = col;
            ErrorCount++;
        }

        public void Error(Statement s, string msg, params object[] args)
        {
            Contract.Requires(s != null);
            Contract.Requires(msg != null);
            Error(s.Tok, msg, args);
        }

        public static void Warning(string programId, string msg)
        {
            Contract.Requires(msg != null);
            ConsoleColor col = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("{0}: Warning: {1}", string.Format(programId), string.Format(msg));
            Console.ForegroundColor = col;
        }

        public static void Warning(IToken tok, string msg)
        {
            Contract.Requires(msg != null);
            Contract.Requires(tok != null);
            ConsoleColor col = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("{0}({1},{2}): Warning: {3}",
                DafnyOptions.Clo.UseBaseNameForFileName ? System.IO.Path.GetFileName(tok.filename) : tok.filename, tok.line, tok.col - 1,
                string.Format(msg));
            Console.ForegroundColor = col;
        }

        public static void Warning(Statement s, string msg)
        {
            Contract.Requires(msg != null);
            Contract.Requires(s != null);
            Warning(s.Tok, msg);
        }

        public static void Message(string msg, IToken tok = null)
        {
            Contract.Requires(msg != null);
            ConsoleColor col = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("DEBUG: {0}",
                string.Format(msg));
            Console.ForegroundColor = col;
        }
    }

    public class Interpreter : ResolutionErrorReporter
    {
        private Program tacnyProgram;

        private SolutionList solution_list = null;

        public Interpreter(Program tacnyProgram)
        {
            Contract.Requires(tacnyProgram != null);
            this.tacnyProgram = tacnyProgram;
            this.solution_list = new SolutionList();
        }

        public string ResolveProgram()
        {
            string err = null;

            if (tacnyProgram.tactics.Count > 0)
            {
                foreach (var member in tacnyProgram.members)
                {
                    err = ScanMemberBody(member);
                    if (err != null)
                        return err;
                }

                if (solution_list != null)
                {
                    err = VerifySolutionList(solution_list);

                    return err;
                }
            }
            tacnyProgram.ResolveProgram();
            tacnyProgram.VerifyProgram();
            return err;
        }

        /// <summary>
        /// Traverses the tree. Generates, resolves and verifies each leaf node until a 
        /// valid proof is found
        /// </summary>
        /// <param name="solution_tree"></param>
        /// <returns></returns>
        private string VerifySolutionList(SolutionList solution_list)
        {
            string err = null;

            List<Solution> final = new List<Solution>(); // list of verified solutions
            Dafny.Program program;
            foreach (var list in solution_list.GetFinal())
            {
                int index = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    index = i;
                    var solution = list[i];
                    if (solution.isFinal)
                    {
                        final.Add(solution);
                        break;
                    }
                    tacnyProgram.program = tacnyProgram.parseProgram();
                    program = tacnyProgram.program;
                    solution.GenerateProgram(ref program);

                    tacnyProgram.ClearBody(solution.state.md);
                    err = tacnyProgram.ResolveProgram();
                    if (err != null)
                        Warning(tacnyProgram.programId, err);
                    tacnyProgram.VerifyProgram();
                    if (!tacnyProgram.HasError())
                    {
                        final.Add(solution);
                        break;
                    }
                    if (index == list.Count - 1)
                        final.Add(solution);
                }
            }

            tacnyProgram.program = tacnyProgram.parseProgram();
            program = tacnyProgram.program;
            foreach (var solution in final)
                solution.GenerateProgram(ref program);

            err = tacnyProgram.VerifyProgram();
            if (err != null)
                return err;
            tacnyProgram.MaybePrintProgram(DafnyOptions.O.DafnyPrintResolvedFile);


            return null;
        }

        // will probably require some handling to unresolvable tactics
        private string ScanMemberBody(MemberDecl md)
        {
            Method m = md as Method;
            if (m == null)
                return null;

            foreach (Statement st in m.Body.Body)
            {
                UpdateStmt us = st as UpdateStmt;
                if (us != null)
                {
                    if (tacnyProgram.IsTacticCall(us))
                    {
                        string err = Action.ResolveTactic(tacnyProgram.GetTactic(us), us, md, tacnyProgram, out solution_list);
                        if (err != null)
                            return err;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Generates a solution tree from the tactics body
        /// </summary>
        /// <param name="tac"></param>
        /// <param name="tac_call"></param>
        /// <param name="md"></param>
        /// <returns>null on success otherwise an error message is returned</returns>
        private string ResolveTacticBody(Tactic tac, UpdateStmt tac_call, MemberDecl md)
        {
            Contract.Requires(tac != null);
            Contract.Requires(tac_call != null);
            Contract.Requires(md != null);

            //local solution list
            SolutionList solution_list = new SolutionList(new Solution(new Action(md, tac, tac_call, tacnyProgram)));
            string err = null;

            while (!solution_list.IsFinal())
            {
                List<Solution> result = null;

                err = Action.ResolveOne(ref result, solution_list.plist);
                if (err != null)
                    return err;

                if (result.Count > 0)
                    solution_list.AddRange(result);
                else
                    break;
            }

            this.solution_list.AddFinal(solution_list.plist);
            return null;
        }
    }
}