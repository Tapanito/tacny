﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using Microsoft.Dafny;
using Dafny = Microsoft.Dafny;
using System.Diagnostics;
namespace LazyTacny
{
    public class SolutionList
    {

        public List<Solution> plist;

        private List<List<Solution>> final; // a list of solutions ofr each tactic

        public SolutionList()
        {
            plist = new List<Solution>();
            final = new List<List<Solution>>();
        }

        public SolutionList(Solution solution)
        {
            Contract.Requires(solution != null);
            plist = new List<Solution>() { solution };
            final = new List<List<Solution>>();
        }

        public void Add(Solution solution)
        {
            plist.Add(solution);
        }

        public void AddRange(List<Solution> solutions)
        {
            // remove non final solutions
            List<Solution> tmp = new List<Solution>();
            foreach (var item in plist)
            {
                if (item.state.DynamicContext.IsResolved())
                    tmp.Add(item);
            }
            plist.Clear();
            plist = tmp;
            //plist.Clear();
            plist.AddRange(solutions);
        }

        public void AddFinal(List<Solution> solutions)
        {
            final.Add(new List<Solution>(solutions.ToArray()));
        }

        public bool IsFinal()
        {
            foreach (var item in plist)
                if (!item.isFinal)
                    return false;
            return true;
        }

        public void SetIsFinal()
        {
            foreach (var item in plist)
                item.isFinal = true;
        }

        public void UnsetFinal()
        {
            foreach (var item in plist)
                item.isFinal = false;
        }

        public List<List<Solution>> GetFinal()
        {
            return final;
        }

        public void Fin()
        {
            if (plist.Count > 0)
            {
                AddFinal(plist);
                plist.Clear();
            }

        }
    }


    public class Solution
    {
        public Atomic state;
        private Solution _parent = null;
        public Solution parent
        {
            set { _parent = value; }
            get { return _parent; }
        }

        public bool isFinal = false;

        public Solution(Atomic state, Solution parent = null)
            : this(state, false, parent)
        { }

        public Solution(Atomic state, bool isFinal, Solution parent)
        {
            this.state = state;
            this.isFinal = isFinal;
            this.parent = parent;
        }

        [Pure]
        public bool IsResolved()
        {
            return state.DynamicContext.IsResolved();
        }


        public Program GenerateProgram(Function func, Program prog)
        {
            ClassDecl curDecl;
            for (int i = 0; i < prog.DefaultModuleDef.TopLevelDecls.Count; i++)
            {
                curDecl = prog.DefaultModuleDef.TopLevelDecls[i] as ClassDecl;
                if (curDecl != null)
                {
                    // scan each member for tactic calls and resolve if found
                    for (int j = 0; j < curDecl.Members.Count; j++)
                    {
                        if (curDecl.Members[i].Name == func.Name)
                            curDecl.Members[i] = func;
                    }

                    prog.DefaultModuleDef.TopLevelDecls[i] = RemoveTactics(curDecl);
                }
            }

            return prog;
        }

        public string GenerateProgram(ref Dafny.Program prog, bool isFinal = false)
        {
            Debug.WriteLine("Generating Dafny program");
            Method method = null;
            List<Dafny.Program> prog_list = new List<Dafny.Program>();
            Atomic ac = state;// state.Copy();
            MemberDecl newMemberDecl = null;
            ac.Fin();
            if (!ac.IsFunction)
            {
                method = Tacny.Program.FindMember(prog, ac.DynamicContext.md.Name) as Method;
                if (method == null)
                    throw new Exception("Method not found");
                UpdateStmt tac_call = ac.GetTacticCall();
                List<Statement> body = method.Body.Body;
                body = InsertSolution(body, tac_call, ac.GetResolved());
                if (body == null)
                    return null;
                if (!isFinal)
                {
                    for (int i = 0; i < body.Count; i++)
                    {
                        if (body[i] is UpdateStmt)
                        {
                            if (state.StaticContext.program.IsTacticCall(body[i] as UpdateStmt))
                                body.RemoveAt(i);
                        }
                    }
                }

                //URGENT TODO: function generation
                newMemberDecl = GenerateMethod(method, body, ac.DynamicContext.newTarget as Method);
            }
            else
            {
                newMemberDecl = ac.GetNewTarget();
            }
            ClassDecl curDecl;
            for (int i = 0; i < prog.DefaultModuleDef.TopLevelDecls.Count; i++)
            {
                curDecl = prog.DefaultModuleDef.TopLevelDecls[i] as ClassDecl;
                if (curDecl != null)
                {
                    // scan each member for tactic calls and resolve if found
                    for (int j = 0; j < curDecl.Members.Count; j++)
                    {


                        if (curDecl.Members[j].Name == newMemberDecl.Name)
                            curDecl.Members[j] = newMemberDecl;
                    }

                    prog.DefaultModuleDef.TopLevelDecls[i] = RemoveTactics(curDecl);
                }
            }

            Debug.WriteLine("Dafny program generated");
            return null;
        }

        private static Method GenerateMethod(Method oldMd, List<Statement> body, Method source = null)
        {
            Method src = source == null ? oldMd : source;
            BlockStmt mdBody = new BlockStmt(src.Body.Tok, src.Body.EndTok, body);
            System.Type type = src.GetType();
            if (type == typeof(Lemma))
                return new Lemma(src.tok, src.Name, src.HasStaticKeyword, src.TypeArgs, src.Ins, src.Outs, src.Req, src.Mod,
                src.Ens, src.Decreases, mdBody, src.Attributes, src.SignatureEllipsis);
            else if (type == typeof(CoLemma))
                return new CoLemma(src.tok, src.Name, src.HasStaticKeyword, src.TypeArgs, src.Ins, src.Outs, src.Req, src.Mod,
                src.Ens, src.Decreases, mdBody, src.Attributes, src.SignatureEllipsis);
            else
                return new Method(src.tok, src.Name, src.HasStaticKeyword, src.IsGhost,
                    src.TypeArgs, src.Ins, src.Outs, src.Req, src.Mod, src.Ens, src.Decreases,
                    mdBody, src.Attributes, src.SignatureEllipsis);
        }

        public static void PrintSolution(Solution solution)
        {
            Dafny.Program prog = solution.state.StaticContext.program.ParseProgram();
            solution.GenerateProgram(ref prog);
            solution.state.StaticContext.program.ClearBody(solution.state.DynamicContext.md);
            Console.WriteLine(String.Format("Tactic call {0} in {1} results: ", solution.state.DynamicContext.tactic.Name, solution.state.DynamicContext.md.Name));
            solution.state.StaticContext.program.PrintMember(prog, solution.state.StaticContext.md.Name);
        }

        private static List<Statement> InsertSolution(List<Statement> body, UpdateStmt tac_call, List<Statement> solution)
        {
            WhileStmt ws = null;
            BlockStmt bs = null;
            int index = FindTacCall(body, tac_call);
            if (index == -1)
                return null;

            List<Statement> newBody = new List<Statement>();
            if (index == 0)
            {
                Statement[] tmp = body.ToArray();
                newBody = new List<Statement>(tmp);
                newBody.RemoveAt(index);
                newBody.InsertRange(0, solution);
                return newBody;
            }

            // check where from tac_call has been made
            int i = index + 1;
            while (i < body.Count)
            {
                Statement stmt = body[i];
                bs = stmt as BlockStmt;
                // if we found a block statement check behind to find the asociated while statement
                if (bs != null)
                {
                    int j = index;
                    while (j >= 0)
                    {
                        Statement stmt_2 = body[j];
                        ws = stmt_2 as WhileStmt;
                        if (ws != null)
                            break;

                        else if (!(stmt_2 is UpdateStmt))
                            return null;

                        j--;
                    }
                    break;
                }
                else if (!(stmt is UpdateStmt))
                    return null;

                i++;
            }
            //// tactic called from a while statement
            if (ws != null && bs != null)
            {
                Statement[] tmp = body.ToArray();
                int l_bound = body.IndexOf(ws);
                int u_boud = body.IndexOf(bs);
                // tactic called in a while statement should 
                //  return only a single solution item which is a WhileStmt
                if (solution.Count > 0)
                {
                    WhileStmt mod_ws = (WhileStmt)solution[0];
                    mod_ws = new WhileStmt(mod_ws.Tok, mod_ws.EndTok, mod_ws.Guard, mod_ws.Invariants,
                        mod_ws.Decreases, mod_ws.Mod, bs);
                    tmp[l_bound] = mod_ws;
                }
                else
                {
                    ws = new WhileStmt(ws.Tok, ws.EndTok, ws.Guard, ws.Invariants, ws.Decreases, ws.Mod, bs);
                    tmp[l_bound] = ws;
                }
                l_bound++;


                // for now remove everything between while stmt and block stmt
                while (l_bound <= u_boud)
                {
                    tmp[l_bound] = null;
                    l_bound++;
                }

                foreach (Statement st in tmp)
                    if (st != null)
                        newBody.Add(st);
            }
            else
            {
                Statement[] tmp = body.ToArray();
                newBody = new List<Statement>(tmp);
                newBody.RemoveAt(index);
                newBody.InsertRange(index, solution);
            }

            return newBody;
        }

        private static int FindTacCall(List<Statement> body, UpdateStmt tac_call)
        {
            for (int j = 0; j < body.Count; j++)
            {
                UpdateStmt us = body[j] as UpdateStmt;
                if (us != null)
                    if (us.Tok.line == tac_call.Tok.line && us.Tok.col == tac_call.Tok.col)
                        return j;
            }
            return -1;
        }

        private static TopLevelDecl RemoveTactics(ClassDecl cd)
        {
            List<MemberDecl> mdl = new List<MemberDecl>();
            foreach (MemberDecl md in cd.Members)
                if (!(md is ITactic))
                    mdl.Add(md);

            return new ClassDecl(cd.tok, cd.Name, cd.Module, cd.TypeArgs, mdl, cd.Attributes, cd.TraitsTyp);
        }


        private static TopLevelDecl ExtractContext(MemberDecl md, ClassDecl cd)
        {
            Dictionary<string, MemberDecl> context = new Dictionary<string, MemberDecl>();
            if (md is Method)
            {
                Method method = Util.Copy.CopyMethod(md as Method);
                // check what member declarations are called in the pre condition
                List<MaybeFreeExpression> lMfe = method.Req;
                foreach (var mfe in lMfe)
                {
                    Expression exp = mfe.E;
                }
            }
            return null;
        }
    }
}