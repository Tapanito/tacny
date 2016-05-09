﻿using Microsoft.Dafny;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Diagnostics;
using System.Linq;
using Dafny = Microsoft.Dafny;
using Microsoft.Boogie;
using System.Numerics;
using Tacny;

namespace LazyTacny
{

    [ContractClass(typeof(AtomicLazyContract))]
    public interface IAtomicLazyStmt
    {
        IEnumerable<Solution> Resolve(Statement st, Solution solution);
    }

    [ContractClassFor(typeof(IAtomicLazyStmt))]
    // Validate the input before execution
    public abstract class AtomicLazyContract : IAtomicLazyStmt
    {

        public IEnumerable<Solution> Resolve(Statement st, Solution solution)
        {
            Contract.Requires<ArgumentNullException>(st != null && solution != null);
            yield break;

        }
    }

    public class Atomic
    {
        public readonly StaticContext StaticContext;
        public DynamicContext DynamicContext;
        private Strategy SearchStrat = Strategy.BFS;
        public bool IsFunction = false;

        public Atomic()
        {
            this.DynamicContext = new DynamicContext();
            this.StaticContext = new StaticContext();
        }
        protected Atomic(Atomic ac)
        {
            Contract.Requires(ac != null);

            this.DynamicContext = ac.DynamicContext;
            this.StaticContext = ac.StaticContext;
            this.SearchStrat = ac.SearchStrat;
        }

        public Atomic(MemberDecl md, ITactic tactic, UpdateStmt tac_call, Tacny.Program program)
        {
            Contract.Requires(md != null);
            Contract.Requires(tactic != null);

            this.DynamicContext = new DynamicContext(md, tactic, tac_call);
            this.StaticContext = new StaticContext(md, tac_call, program);
        }

        public Atomic(MemberDecl md, ITactic tac, UpdateStmt tac_call, StaticContext globalContext)
        {
            this.DynamicContext = new DynamicContext(md, tac, tac_call);
            this.StaticContext = globalContext;

        }

        public Atomic(DynamicContext localContext, StaticContext globalContext, Strategy searchStrategy, bool isFunction)
        {
            this.StaticContext = globalContext;
            this.DynamicContext = localContext.Copy();
            this.SearchStrat = searchStrategy;
            this.IsFunction = isFunction;
        }
        public void Initialize()
        {

        }

        ~Atomic()
        {

        }
        /// <summary>
        /// Create a deep copy of an action
        /// </summary>
        /// <returns>Action</returns>
        public Atomic Copy()
        {
            Contract.Ensures(Contract.Result<Atomic>() != null);
            return new Atomic(DynamicContext, StaticContext, this.SearchStrat, IsFunction);
        }


        public static Solution ResolveTactic(ITactic tac, UpdateStmt tac_call, MemberDecl md, Tacny.Program tacnyProgram, List<IVariable> variables, List<IVariable> resolved)//, SolutionList result)
        {
            Contract.Requires(tac != null);
            Contract.Requires(tac_call != null);
            Contract.Requires(md != null);
            Contract.Requires(tacnyProgram != null);
            Contract.Requires(tcce.NonNullElements<IVariable>(variables));
            Contract.Requires(tcce.NonNullElements<IVariable>(resolved));
            //Contract.Requires(result != null);
            List<Solution> res = new List<Solution>();
            Atomic ac = new Atomic(md, tac, tac_call, tacnyProgram);
            ac.StaticContext.RegsiterGlobalVariables(variables, resolved);
            // set strategy
            ac.SearchStrat = SearchStrategy.GetSearchStrategy(tac);
            // because solution is verified at the end
            // we can safely terminated after the first item is received
            // TODO
            foreach (var item in ResolveTactic(ac))
            {
                return item;
            }

            return null;

        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="input"></param>
        /// <param name="atomic"></param>
        /// <returns></returns>
        /*
            !!!Search strategies will go in here!!!
            !!! Validation of the results should also go here!!!
         */
        public static IEnumerable<Solution> ResolveTactic(Atomic atomic, bool verify = true)
        {
            Contract.Requires(atomic.DynamicContext.tactic is Tactic);
            if (atomic.DynamicContext.tactic is Tactic)
            {
                ISearch searchStrategy = new SearchStrategy(atomic.SearchStrat);
                foreach (var item in searchStrategy.Search(atomic, verify))
                    yield return item;
            }
            yield break;
        }



        public static Expression ResolveTacticFunction(ITactic tac, UpdateStmt tac_call, MemberDecl md, Tacny.Program tacnyProgram, List<IVariable> variables, List<IVariable> resolved)
        {
            Contract.Requires(tac is TacticFunction);
            List<Solution> res = new List<Solution>();
            Atomic atomic = new Atomic(md, tac, tac_call, tacnyProgram);
            atomic.IsFunction = true;
            atomic.StaticContext.RegsiterGlobalVariables(variables, resolved);
            var tacFun = atomic.DynamicContext.tactic as TacticFunction;
            ExpressionTree expt = ExpressionTree.ExpressionToTree(tacFun.Body);
            atomic.ResolveTacticFunction(expt);
            return expt.TreeToExpression();
            
        }

        /// <summary>
        /// Resolve a block statement
        /// </summary>
        /// <param name="body"></param>
        /// <returns></returns>
        public IEnumerable<Solution> ResolveBody(BlockStmt body)
        {
            Contract.Requires<ArgumentNullException>(body != null);

            Debug.WriteLine("Resolving statement body");
            ISearch strat = new SearchStrategy(this.SearchStrat);
            Atomic ac = this.Copy();
            ac.DynamicContext.tacticBody = body.Body;
            ac.DynamicContext.ResetCounter();
            foreach (var item in strat.Search(ac, false))
            {
                item.state.DynamicContext.tacticBody = this.DynamicContext.tacticBody; // set the body 
                item.state.DynamicContext.tac_call = this.DynamicContext.tac_call;
                item.state.DynamicContext.SetCounter(this.DynamicContext.GetCounter());
                yield return item;
            }
            Debug.WriteLine("Body resolved");

            yield break;
        }

        public static IEnumerable<Solution> ResolveStatement(Solution solution)
        {
            Contract.Requires<ArgumentNullException>(solution != null);
            if (solution.state.DynamicContext.IsResolved())
                yield break;
            // if no statements have been resolved, check the preconditions
            if (solution.state.DynamicContext.IsFirstStatment())
                TacnyContract.ValidateRequires(solution);
            // foreach result yield
            foreach (var result in solution.state.CallAction(solution.state.DynamicContext.GetCurrentStatement(), solution))
            {

                result.parent = solution;
                // increment the counter if the statement has been fully resolved
                if (!result.state.DynamicContext.isPartialyResolved)
                    result.state.DynamicContext.IncCounter();
                yield return result;

            }


            yield break;
        }

        protected IEnumerable<Solution> CallAction(object call, Solution solution)
        {
            foreach (var item in CallAtomic(call, solution))
            {
                StaticContext.program.IncTotalBranchCount(StaticContext.program.currentDebug);
                yield return item;
            }

            yield break;
        }

        protected IEnumerable<Solution> CallAtomic(object call, Solution solution)
        {
            Contract.Requires<ArgumentNullException>(call != null);
            Contract.Requires<ArgumentNullException>(solution != null);

            System.Type type = null;
            Statement st = null;
            ApplySuffix aps = null;
            UpdateStmt us = null;
            if ((st = call as Statement) != null)
                type = StatementRegister.GetStatementType(st);
            else
            {
                if ((aps = call as ApplySuffix) != null)
                {
                    type = StatementRegister.GetStatementType(aps);
                    // convert applySuffix to an updateStmt
                    st = new UpdateStmt(aps.tok, aps.tok, new List<Expression>(), new List<AssignmentRhs>() { new ExprRhs(aps) });
                }
            }

            if (type != null)
            {
                Debug.WriteLine(String.Format("Resolving statement {0}", type.ToString()));
                var qq = Activator.CreateInstance(type, new object[] { this }) as IAtomicLazyStmt;
                if (qq != null)
                {
                    foreach (var res in qq.Resolve(st, solution))
                    {
                        yield return res;
                    }
                }
                else
                {
                    Contract.Assert(false, Util.Error.MkErr(st, 18, type.ToString(), typeof(IAtomicLazyStmt)));
                }
            }
            else // could not determine the statement type, check if it's a nested tactic call
            {
                Debug.WriteLine("Could not determine statement type");
                if (call is TacticVarDeclStmt)
                {
                    Debug.WriteLine("Found tactic variable declaration");
                    foreach (var item in (RegisterLocalVariable(call as TacticVarDeclStmt)))
                        yield return item;
                }
                else if ((us = st as UpdateStmt) != null)
                {
                    // if the statement is nested tactic call
                    if (StaticContext.program.IsTacticCall(us))
                    {
                        Debug.WriteLine("Found nested tactic call");
                        Atomic ac = new Atomic(DynamicContext.md, StaticContext.program.GetTactic(us), DynamicContext.tac_call, StaticContext);
                        // TODO fix nested tactic calls
                        ExprRhs er = us.Rhss[0] as ExprRhs;
                        List<Expression> exps = ((ApplySuffix)er.Expr).Args;
                        Contract.Assert(exps.Count == ac.DynamicContext.tactic.Ins.Count);
                        ac.SetNewTarget(GetNewTarget());
                        for (int i = 0; i < exps.Count; i++)
                        {
                            foreach (var result in ProcessStmtArgument(exps[i]))
                            {
                                ac.AddLocal(ac.DynamicContext.tactic.Ins[i], result);
                            }
                        }
                        List<Solution> sol_list = new List<Solution>();
                        /**
                         * Transfer the results from evaluating the nested tactic
                         */
                        foreach (var item in ResolveTactic(ac, false))
                        {
                            Atomic action = this.Copy();
                            action.SetNewTarget(solution.state.GetNewTarget());
                            foreach (KeyValuePair<Statement, Statement> kvp in item.state.GetResult())
                                action.AddUpdated(kvp.Key, kvp.Value);
                            yield return new Solution(action);
                        }
                    }
                    else if (IsLocalAssignment(us)) // if the updatestmt is asignment
                    {
                        foreach (var result in UpdateLocalVariable(us))
                        {
                            yield return result;
                        }
                    }
                    else if (IsArgumentApplication(us)) // true when tactic is passed as an argument
                    {
                        var name = GetNameSegment(us);
                        var application = GetLocalValueByName(name) as ApplySuffix;
                        var newUpdateStmt = new UpdateStmt(us.Tok, us.EndTok, us.Lhss, new List<AssignmentRhs>() { new ExprRhs(application) });
                        var ac = this.Copy();
                        foreach (var argument in application.Args)
                        {
                            var ns = argument as NameSegment;
                            if (StaticContext.HasGlobalVariable(ns.Name))
                            {
                                var temp = StaticContext.GetGlobalVariable(ns.Name);
                                ac.DynamicContext.AddLocal(new Dafny.Formal(ns.tok, ns.Name, temp.Type, true, temp.IsGhost), ns);
                            }
                        }
                        foreach (var item in ac.CallAction(newUpdateStmt, solution))
                        {
                            yield return item;
                        }
                    }
                    else // insert the statement as is
                    {
                        yield return CallDefaultAction(st);
                    }
                }
                else
                {
                    yield return CallDefaultAction(st);
                }
            }
            yield break;
        }

        public IEnumerable<object> ProcessStmtArgument(Expression argument)
        {
            Contract.Requires<ArgumentNullException>(argument != null);
            NameSegment ns = null;
            ApplySuffix aps = null;
            if ((ns = argument as NameSegment) != null)
            {
                //Contract.Assert(HasLocalWithName(ns), Util.Error.MkErr(ns, 9, ns.Name));
                if (HasLocalWithName(ns))
                    yield return GetLocalValueByName(ns.Name) ?? ns;
                else yield return ns;
            }
            else if ((aps = argument as ApplySuffix) != null)
            {
                // create a VarDeclStmt
                // first create an UpdateStmt
                UpdateStmt us = new UpdateStmt(aps.tok, aps.tok, new List<Expression>(), new List<AssignmentRhs>() { new ExprRhs(aps) });
                // create a unique local variable
                Dafny.LocalVariable lv = new Dafny.LocalVariable(aps.tok, aps.tok, aps.Lhs.tok.val, new BoolType(), false);
                TacticVarDeclStmt tvds = new TacticVarDeclStmt(us.Tok, us.EndTok, new List<Dafny.LocalVariable>() { lv }, us);
                foreach (var item in CallAction(tvds, new Solution(this.Copy())))
                {
                    var res = item.state.DynamicContext.localDeclarations[lv];
                    item.state.DynamicContext.localDeclarations.Remove(lv);
                    yield return res;
                }
            }
            else if (argument is BinaryExpr || argument is ParensExpression)
            {
                ExpressionTree expt = ExpressionTree.ExpressionToTree(argument);
                ResolveExpression(expt);
                if (IsResolvable(expt))
                    yield return EvaluateExpression(expt);
                else
                    yield return expt.TreeToExpression();
            } else if (argument is ExprDotName)
            {
                var edn = argument as ExprDotName;
                if (HasLocalWithName(edn.Lhs as NameSegment))
                {
                    ns = edn.Lhs as NameSegment;
                    var newLhs = GetLocalValueByName(ns.Name) ?? ns;
                    yield return new ExprDotName(edn.tok, newLhs as Expression, edn.SuffixName, edn.OptTypeArguments);
                } else
                {
                    yield return edn;
                }
            }
            else
            {
                yield return argument;
            }
            yield break;
        }

        private IEnumerable<Solution> RegisterLocalVariable(TacticVarDeclStmt declaration)
        {
            Contract.Requires(declaration != null);
            // if declaration has rhs
            if (declaration.Update != null)
            {
                UpdateStmt rhs = declaration.Update as UpdateStmt;
                // if statement is of type var q;
                if (rhs == null)
                { /* leave the statement */ }
                else
                {
                    foreach (var item in rhs.Rhss)
                    {
                        int index = rhs.Rhss.IndexOf(item);
                        Contract.Assert(declaration.Locals.ElementAtOrDefault(index) != null, Util.Error.MkErr(declaration, 8));
                        ExprRhs exprRhs = item as ExprRhs;
                        // if the declaration is literal expr (e.g. tvar q := 1)
                        Dafny.LiteralExpr litExpr = exprRhs.Expr as Dafny.LiteralExpr;
                        if (litExpr != null)
                        {
                            AddLocal(declaration.Locals[index], litExpr);
                            yield return new Solution(this.Copy());
                        }
                        else
                        {
                            foreach (var result in ProcessStmtArgument(exprRhs.Expr))
                            {
                                AddLocal(declaration.Locals[index], result);
                                yield return new Solution(this.Copy());
                            }
                        }
                    }
                }
            }
            else
            {
                foreach (var item in declaration.Locals)
                    AddLocal(item as IVariable, null);
                yield return new Solution(this.Copy());

            }
            yield break;
        }

        private IEnumerable<Solution> UpdateLocalVariable(UpdateStmt updateStmt)
        {
            Contract.Requires(updateStmt != null);
            Debug.WriteLine("Updating local variable");
            // if statement is of type var q;
            Contract.Assert(updateStmt.Lhss.Count == updateStmt.Rhss.Count, Util.Error.MkErr(updateStmt, 8));
            for (int i = 0; i < updateStmt.Lhss.Count; i++)
            {
                NameSegment variable = updateStmt.Lhss[i] as NameSegment;
                Contract.Assert(variable != null, Util.Error.MkErr(updateStmt, 5, typeof(NameSegment), updateStmt.Lhss[i].GetType()));
                Contract.Assert(HasLocalWithName(variable), Util.Error.MkErr(updateStmt, 9, variable.Name));
                // get the key of the variable
                IVariable local = GetLocalKeyByName(variable);
                foreach (var item in updateStmt.Rhss)
                {
                    // unfold the rhs
                    ExprRhs exprRhs = item as ExprRhs;
                    if (exprRhs != null)
                    {
                        // if the expression is a literal value update the value
                        Dafny.LiteralExpr litVal = exprRhs.Expr as Dafny.LiteralExpr;
                        if (litVal != null)
                        {
                            AddLocal(local, litVal);
                            yield return new Solution(this.Copy());
                        }
                        else
                        { // otherwise process the expression
                            foreach (var result in ProcessStmtArgument(exprRhs.Expr))
                            {
                                AddLocal(local, result);
                                yield return new Solution(this.Copy());
                            }
                        }
                    }
                }
            }

            yield break;
        }

        /// <summary>
        /// Clear local variables, and fill them with tactic arguments. Use with caution.
        /// </summary>
        public void FillTacticInputs()
        {
            DynamicContext.FillTacticInputs();
        }

        protected void InitArgs(Statement st, out List<Expression> call_arguments)
        {
            Contract.Requires(st != null);
            Contract.Ensures(Contract.ValueAtReturn<List<Expression>>(out call_arguments) != null);
            IVariable lv;
            InitArgs(st, out lv, out call_arguments);
        }

        /// <summary>
        /// Extract statement arguments and local variable definition
        /// </summary>
        /// <param name="st">Atomic statement</param>
        /// <param name="lv">Local variable</param>
        /// <param name="call_arguments">List of arguments</param>
        /// <returns>Error message</returns>
        protected void InitArgs(Statement st, out IVariable lv, out List<Expression> call_arguments)
        {
            Contract.Requires(st != null);
            Contract.Ensures(Contract.ValueAtReturn<List<Expression>>(out call_arguments) != null);
            lv = null;
            call_arguments = null;
            TacticVarDeclStmt tvds = null;
            UpdateStmt us = null;
            TacnyBlockStmt tbs = null;
            // tacny variables should be declared as tvar or tactic var
            if (st is VarDeclStmt)
                Contract.Assert(false, Util.Error.MkErr(st, 13));

            if ((tvds = st as TacticVarDeclStmt) != null)
            {
                lv = tvds.Locals[0];
                call_arguments = GetCallArguments(tvds.Update as UpdateStmt);

            }
            else if ((us = st as UpdateStmt) != null)
            {
                if (us.Lhss.Count == 0)
                    call_arguments = GetCallArguments(us);
                else
                {
                    NameSegment ns = (NameSegment)us.Lhss[0];
                    if (HasLocalWithName(ns))
                    {
                        lv = GetLocalKeyByName(ns);
                        call_arguments = GetCallArguments(us);
                    }
                    else
                        Util.Printer.Error(st, "Local variable {0} is not declared", ns.Name);
                }
            }
            else if ((tbs = st as TacnyBlockStmt) != null)
            {
                ParensExpression pe = tbs.Guard as ParensExpression;
                if (pe != null)
                    call_arguments = new List<Expression>() { pe.E };
                else
                    call_arguments = new List<Expression>() { tbs.Guard };
            }
            else
                Util.Printer.Error(st, "Wrong number of method result arguments; Expected {0} got {1}", 1, 0);

        }

        private Solution CallDefaultAction(Statement st)
        {
            Contract.Requires(st != null);

            Atomic state = this.Copy();
            state.AddUpdated(st, st);
            return new Solution(state, null);
        }

        /// <summary>
        /// Find closest while statement to the tactic call
        /// </summary>
        /// <param name="tac_stmt">Tactic call</param>
        /// <param name="member">Method</param>
        /// <returns>WhileStmt</returns>
        protected static WhileStmt FindWhileStmt(Statement tac_stmt, MemberDecl member)
        {
            Contract.Requires(tac_stmt != null);
            Contract.Requires(member != null);

            Method m = (Method)member;

            int index = m.Body.Body.IndexOf(tac_stmt);
            if (index <= 0)
                return null;

            while (index >= 0)
            {
                Statement stmt = m.Body.Body[index];

                WhileStmt ws = stmt as WhileStmt;
                if (ws != null)
                    return ws;

                index--;
            }
            return null;
        }

        protected static List<Expression> GetCallArguments(UpdateStmt us)
        {
            Contract.Requires(us != null);
            ExprRhs er = (ExprRhs)us.Rhss[0];
            return ((ApplySuffix)er.Expr).Args;
        }

        protected bool HasLocalWithName(NameSegment ns)
        {
            Contract.Requires<ArgumentNullException>(ns != null);
            return DynamicContext.HasLocalWithName(ns);
        }

        protected object GetLocalValueByName(NameSegment ns)
        {
            Contract.Requires<ArgumentNullException>(ns != null);
            return DynamicContext.GetLocalValueByName(ns.Name);
        }

        protected object GetLocalValueByName(IVariable variable)
        {
            Contract.Requires<ArgumentNullException>(variable != null);
            return DynamicContext.GetLocalValueByName(variable);
        }

        protected object GetLocalValueByName(string name)
        {
            Contract.Requires<ArgumentNullException>(name != null);
            return DynamicContext.GetLocalValueByName(name);
        }

        protected IVariable GetLocalKeyByName(NameSegment ns)
        {
            Contract.Requires<ArgumentNullException>(ns != null);
            return DynamicContext.GetLocalKeyByName(ns.Name);
        }

        protected IVariable GetLocalKeyByName(string name)
        {
            Contract.Requires<ArgumentNullException>(name != null);
            return DynamicContext.GetLocalKeyByName(name);
        }

        public void Fin()
        {
            StaticContext.resolved.Clear();
            StaticContext.resolved.AddRange(DynamicContext.generatedStatements.Values.ToArray());
            StaticContext.newTarget = DynamicContext.newTarget;
        }

        public void AddLocal(IVariable lv, object value)
        {
            Contract.Requires<ArgumentNullException>(lv != null);
            // globalContext.program.IncTotalBranchCount(globalContext.program.currentDebug);
            DynamicContext.AddLocal(lv, value);
        }

        protected static Token CreateToken(string val, int line, int col)
        {
            var tok = new Token(line, col);
            tok.val = val;
            return tok;
        }

        public List<Statement> GetResolved()
        {
            return StaticContext.resolved;
        }

        public UpdateStmt GetTacticCall()
        {
            return DynamicContext.tac_call;
        }

        public void AddUpdated(Statement key, Statement value)
        {
            Contract.Requires(key != null && value != null);
            //  globalContext.program.IncTotalBranchCount(globalContext.program.currentDebug);
            DynamicContext.AddUpdated(key, value);
        }

        public void RemoveUpdated(Statement key)
        {
            Contract.Requires(key != null);
            DynamicContext.RemoveUpdated(key);
        }

        public Statement GetUpdated(Statement key)
        {
            Contract.Requires(key != null);
            return DynamicContext.GetUpdated(key);
        }

        public List<Statement> GetAllUpdated()
        {
            Contract.Ensures(Contract.Result<List<Statement>>() != null);
            return DynamicContext.GetAllUpdated();
        }

        public Dictionary<Statement, Statement> GetResult()
        {
            return DynamicContext.generatedStatements;
        }

        public bool IsFinal(List<Solution> solution_list)
        {
            Contract.Requires<ArgumentNullException>(solution_list != null);
            foreach (var item in solution_list)
            {
                if (!item.isFinal)
                    return false;
            }

            return true;
        }

        public MemberDecl GetNewTarget()
        {
            return DynamicContext.newTarget;
        }

        public void SetNewTarget(MemberDecl new_target)
        {
            DynamicContext.newTarget = new_target;
        }
        /// <summary>
        /// Creates a new tactic from a given tactic body and updates the context
        /// </summary>
        /// <param name="tac"></param>
        /// <param name="newBody"></param>
        /// <param name="decCounter"></param>
        /// <returns></returns>
        //protected Solution CreateTactic(List<Statement> newBody, bool decCounter = true)
        //{
        //    Contract.Ensures(Contract.Result<Solution>() != null);
        //    Tactic tac = dynamicContext.tactic;
        //    Tactic newTac = new Tactic(tac.tok, tac.Name, tac.HasStaticKeyword,
        //                                tac.TypeArgs, tac.Ins, tac.Outs, tac.Req, tac.Mod, tac.Ens,
        //                                tac.Decreases, new BlockStmt(tac.Body.Tok, tac.Body.EndTok, newBody),
        //                                tac.Attributes, tac.SignatureEllipsis);
        //    Atomic newAtomic = this.Copy();
        //    newAtomic.dynamicContext.tactic = newTac;
        //    newAtomic.dynamicContext.tacticBody = newBody;
        //    /* HACK */
        //    // decrase the tactic body counter
        //    // so the interpreter would execute newly inserted atomic
        //    if (decCounter)
        //        newAtomic.dynamicContext.DecCounter();
        //    return new Solution(newAtomic);
        //}

        /// <summary>
        /// Replaces the statement at current body counter with a new statement
        /// </summary>
        /// <param name="oldBody"></param>
        /// <param name="newStatement"></param>
        /// <returns></returns>
        protected List<Statement> ReplaceCurrentAtomic(Statement newStatement)
        {
            Contract.Requires(newStatement != null);
            Contract.Ensures(Contract.Result<List<Statement>>() != null);
            int index = DynamicContext.GetCounter();
            List<Statement> newBody = DynamicContext.GetFreshTacticBody();
            newBody[index] = newStatement;
            return newBody;
        }

        protected List<Statement> ReplaceCurrentAtomic(List<Statement> list)
        {
            Contract.Requires(list != null);
            Contract.Ensures(Contract.Result<List<Statement>>() != null);
            int index = DynamicContext.GetCounter();
            List<Statement> newBody = DynamicContext.GetFreshTacticBody();
            newBody.RemoveAt(index);
            newBody.InsertRange(index, list);
            return newBody;
        }

        protected Expression EvaluateExpression(ExpressionTree expt)
        {
            Contract.Requires(expt != null);
            if (expt.IsLeaf())
            {
                return EvaluateLeaf(expt) as Dafny.LiteralExpr;
            }
            else
            {
                BinaryExpr bexp = tcce.NonNull<BinaryExpr>(expt.data as BinaryExpr);
                if (BinaryExpr.IsEqualityOp(bexp.Op))
                {
                    var boolVal = EvaluateEqualityExpression(expt);
                    return new Dafny.LiteralExpr(new Token(), boolVal);
                }
                else
                {
                    Dafny.LiteralExpr lhs = EvaluateExpression(expt.lChild) as Dafny.LiteralExpr;
                    Dafny.LiteralExpr rhs = EvaluateExpression(expt.rChild) as Dafny.LiteralExpr;
                    // for now asume lhs and rhs are integers
                    BigInteger l = (BigInteger)lhs.Value;
                    BigInteger r = (BigInteger)rhs.Value;

                    BigInteger res = 0;


                    switch (bexp.Op)
                    {
                        case BinaryExpr.Opcode.Sub:
                            res = BigInteger.Subtract(l, r);
                            break;
                        case BinaryExpr.Opcode.Add:
                            res = BigInteger.Add(l, r);
                            break;
                        case BinaryExpr.Opcode.Mul:
                            res = BigInteger.Multiply(l, r);
                            break;
                        case BinaryExpr.Opcode.Div:
                            res = BigInteger.Divide(l, r);
                            break;
                    }

                    return new Dafny.LiteralExpr(lhs.tok, res);
                }
            }
        }

        /// <summary>
        /// Evalutate an expression tree
        /// </summary>
        /// <param name="expt"></param>
        /// <returns></returns>
        public bool EvaluateEqualityExpression(ExpressionTree expt)
        {
            Contract.Requires(expt != null);
            // if the node is leaf, cast it to bool and return
            if (expt.IsLeaf())
            {
                Dafny.LiteralExpr lit = EvaluateLeaf(expt) as Dafny.LiteralExpr;
                return lit.Value is bool ? (bool)lit.Value : false;
            }
            // left branch only
            else if (expt.lChild != null && expt.rChild == null)
                return EvaluateEqualityExpression(expt.lChild);
            // if there is no more nesting resolve the expression
            else if (expt.lChild.IsLeaf() && expt.rChild.IsLeaf())
            {
                Dafny.LiteralExpr lhs = null;
                Dafny.LiteralExpr rhs = null;
                lhs = EvaluateLeaf(expt.lChild) as Dafny.LiteralExpr;
                rhs = EvaluateLeaf(expt.rChild) as Dafny.LiteralExpr;
                if (!lhs.GetType().Equals(rhs.GetType()))
                    return false;
                BinaryExpr bexp = tcce.NonNull<BinaryExpr>(expt.data as BinaryExpr);
                int res = -1;
                if (lhs.Value is BigInteger)
                {
                    BigInteger l = (BigInteger)lhs.Value;
                    BigInteger r = (BigInteger)rhs.Value;
                    res = l.CompareTo(r);
                }
                else if (lhs.Value is string)
                {
                    string l = lhs.Value as string;
                    string r = rhs.Value as string;
                    res = l.CompareTo(r);
                }
                else if (lhs.Value is bool)
                {
                    res = ((bool)lhs.Value).CompareTo((bool)rhs.Value);
                }

                if (bexp.Op == BinaryExpr.Opcode.Eq)
                    return res == 0;
                else if (bexp.Op == BinaryExpr.Opcode.Neq)
                    return res != 0;
                else if (bexp.Op == BinaryExpr.Opcode.Ge)
                    return res >= 0;
                else if (bexp.Op == BinaryExpr.Opcode.Gt)
                    return res > 0;
                else if (bexp.Op == BinaryExpr.Opcode.Le)
                    return res <= 0;
                else if (bexp.Op == BinaryExpr.Opcode.Lt)
                    return res < 0;
            }
            else // evaluate a nested expression
            {
                BinaryExpr bexp = tcce.NonNull<BinaryExpr>(expt.data as BinaryExpr);
                if (bexp.Op == BinaryExpr.Opcode.And)
                    return EvaluateEqualityExpression(expt.lChild) && EvaluateEqualityExpression(expt.rChild);
                else if (bexp.Op == BinaryExpr.Opcode.Or)
                    return EvaluateEqualityExpression(expt.lChild) || EvaluateEqualityExpression(expt.rChild);
            }
            return false;
        }

        protected void ResolveTacticFunction(ExpressionTree guard)
        {
            Contract.Requires(guard != null);
            if (guard.IsLeaf())
            {
                Expression result; // potential encapsulation problems
                if (guard.data is ApplySuffix)
                {
                    var type = StatementRegister.GetAtomicType(guard.data as ApplySuffix);
                    if (type != StatementRegister.Atomic.UNDEFINED)
                    {
                        foreach (var solution in ProcessStmtArgument(guard.data))
                        {
                            guard.data = solution as Expression;
                            break;
                        }
                    } else
                    {
                        var aps = guard.data as ApplySuffix;
                        if(aps.Lhs is ExprDotName)
                        {
                            foreach(var solution in ProcessStmtArgument(aps.Lhs))
                            {
                                guard.data = new ApplySuffix(aps.tok, solution as Expression, aps.Args);
                                break;
                            }
                        }
                            
                    }
                } else if(guard.data is FunctionCallExpr)
                {
                    var qq = guard.data as FunctionCallExpr;
                }
                else {
                    foreach (var solution in ProcessStmtArgument(guard.data))
                    {
                        guard.data = solution as Expression;
                        break;
                    }   
                }

            }
            else
            {
                ResolveTacticFunction(guard.lChild);
                if (guard.rChild != null)
                    ResolveTacticFunction(guard.rChild);
            }
        }

        /// <summary>
        /// Evaluate a leaf node
        /// TODO: support for call evaluation
        /// </summary>
        /// <param name="expt"></param>
        /// <returns></returns>
        protected object EvaluateLeaf(ExpressionTree expt)
        {
            Contract.Requires(expt != null && expt.IsLeaf());
            if (expt.data is NameSegment || expt.data is ApplySuffix)
            {
                // fix me
                foreach (var item in ProcessStmtArgument(expt.data))
                    return item;
            }
            else if (expt.data is Dafny.LiteralExpr)
                return expt.data;

            return null;
        }

        /// <summary>
        /// Resolve all variables in expression to either literal values
        /// or to orignal declared nameSegments
        /// </summary>
        /// <param name="guard"></param>
        /// <returns></returns>
        protected void ResolveExpression(ExpressionTree guard)
        {
            Contract.Requires(guard != null);
            if (guard.IsLeaf())
            {
                Expression newNs; // potential encapsulation problems
                var result = EvaluateLeaf(guard);

                // we only need to replace nameSegments
                if (guard.data is NameSegment)
                {
                    Contract.Assert(result != null);
                    if (result is MemberDecl)
                    {
                        MemberDecl md = result as MemberDecl;
                        newNs = new Dafny.StringLiteralExpr(new Token(), md.Name, true);
                    }
                    else if (result is Dafny.Formal)
                    {
                        var tmp = result as Dafny.Formal;
                        newNs = new NameSegment(tmp.tok, tmp.Name, null);
                    }
                    else if (result is NameSegment)
                    {
                        newNs = result as NameSegment;
                    }
                    else
                    {
                        newNs = result as Dafny.LiteralExpr;
                    }
                    guard.data = newNs;
                }
            }
            else
            {
                ResolveExpression(guard.lChild);
                if (guard.rChild != null)
                    ResolveExpression(guard.rChild);
            }
        }
        /// <summary>
        /// Determine whehter the given expression tree can be evaluated.
        /// THe value is true if all leaf nodes have literal values
        /// </summary>
        /// <param name="expt"></param>
        /// <returns></returns>
        [Pure]
        protected bool IsResolvable(ExpressionTree expt)
        {
            Contract.Requires(expt.isRoot());
            List<Expression> leafs = expt.GetLeafs();

            foreach (var leaf in leafs)
            {
                if (leaf is NameSegment)
                {
                    NameSegment ns = leaf as NameSegment;
                    object local = GetLocalValueByName(ns);
                    if (!(local is Dafny.LiteralExpr))
                        return false;
                }
                else if (leaf is Dafny.LiteralExpr || leaf is ApplySuffix)
                {
                    continue;
                }
                else
                {
                    return false;
                }
            }
            return true;
        }

        public Solution AddNewStatement<T>(T oldValue, T newValue) where T : Statement
        {
            var ac = this.Copy();
            ac.AddUpdated(oldValue, newValue);
            return new Solution(ac);
        }

        public Solution AddNewLocal<T>(IVariable variable, T value) where T : class
        {
            var ac = this.Copy();
            ac.AddLocal(variable, value);
            return new Solution(ac);
        }

        /// <summary>
        /// Generate a Dafny program and verify it
        /// </summary>
        public bool ResolveAndVerify(Solution solution)
        {
            Contract.Requires<ArgumentNullException>(solution != null);

            Dafny.Program prog = StaticContext.program.ParseProgram();
            solution.GenerateProgram(ref prog);
            StaticContext.program.ClearBody(DynamicContext.md);
#if !DEBUG
            staticContext.program.PrintMember(prog, solution.state.staticContext.md.Name);
#endif
            if (!StaticContext.program.ResolveProgram())
                return false;
            StaticContext.program.VerifyProgram();
            return true;

        }



        /// <summary>
        /// Register datatype ctor vars as locals
        /// </summary>
        /// <param name="datatype"></param>
        /// <param name="index"></param>
        public void RegisterLocals(DatatypeDecl datatype, int index, Dictionary<string, Dafny.Type> ctorTypes = null)
        {
            Contract.Requires(datatype != null);
            Contract.Requires(index + 1 <= datatype.Ctors.Count);

            foreach (var formal in datatype.Ctors[index].Formals)
            {
                // register globals as name segments
                // registed the ctor argument with the correct type
                if (ctorTypes != null)
                {
                    UserDefinedType udt = formal.Type as UserDefinedType;
                    if (udt != null)
                    {
                        if (ctorTypes.ContainsKey(udt.Name))
                        {
                            Dafny.Formal newFormal = new Dafny.Formal(formal.Tok, formal.Name, ctorTypes[udt.Name], formal.InParam, formal.IsGhost);
                            StaticContext.RegsiterGlobalVariable(newFormal);
                        }
                        else
                        {
                            StaticContext.RegsiterGlobalVariable(formal);
                        }
                    }
                    else
                    {
                        StaticContext.RegsiterGlobalVariable(formal);
                    }

                }
                else
                    StaticContext.RegsiterGlobalVariable(formal);
            }
        }

        /// <summary>
        /// Remove datatype ctor vars from locals
        /// </summary>
        /// <param name="datatype"></param>
        /// <param name="index"></param>
        public void RemoveLocals(DatatypeDecl datatype, int index)
        {
            Contract.Requires(datatype != null);
            Contract.Requires(index + 1 <= datatype.Ctors.Count);
            foreach (var formal in datatype.Ctors[index].Formals)
            {
                // register globals as name segments
                StaticContext.RemoveGlobalVariable(formal);
            }
        }

        [Pure]
        protected static bool IsLocalAssignment(UpdateStmt us)
        {
            return us.Lhss.Count > 0 && us.Rhss.Count > 0;
        }

        protected bool IsArgumentApplication(UpdateStmt us)
        {
            var name = GetNameSegment(us);
            if (DynamicContext.HasLocalWithName(name))
                return true;
            return false;
        }




        protected static NameSegment GetNameSegment(UpdateStmt us)
        {
            Contract.Requires(us != null);
            ExprRhs er = us.Rhss[0] as ExprRhs;
            if (er == null)
                return null;
            ApplySuffix asx = er.Expr as ApplySuffix;
            if (asx == null)
                return null;
            return asx.Lhs as NameSegment;
        }
    }
}