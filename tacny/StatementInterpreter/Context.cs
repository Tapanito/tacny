﻿using Microsoft.Dafny;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using Dafny = Microsoft.Dafny;
using Microsoft.Boogie;

namespace Tacny
{
    public class Context
    {
        public MemberDecl md;
        public UpdateStmt tac_call;

        public Context(MemberDecl md, UpdateStmt tac_call)
        {
            Contract.Requires(md != null && tac_call != null);
            this.md = md;
            this.tac_call = tac_call;
        }

    }

    /// <summary>
    /// Local context for the tactic currently being resolved
    /// </summary>
    #region LocalContext
    public class LocalContext : Context
    {
        public Tactic tac = null;  // The called tactic
        public List<Statement> tac_body = new List<Statement>(); // body of the currently worked tactic
        public Dictionary<Dafny.IVariable, object> local_variables = new Dictionary<Dafny.IVariable, object>();
        public Dictionary<Statement, Statement> updated_statements = new Dictionary<Statement, Statement>();
        public Method new_target = null;

        private int tacCounter;

        public LocalContext(MemberDecl md, Tactic tac, UpdateStmt tac_call)
            : base(md, tac_call)
        {
            this.tac = tac;
            this.tac_body = new List<Statement>(tac.Body.Body.ToArray());
            this.tacCounter = 0;
            FillTacticInputs();
        }

        public LocalContext(MemberDecl md, Tactic tac, UpdateStmt tac_call,
            List<Statement> tac_body, Dictionary<Dafny.IVariable, object> local_variables,
            Dictionary<Statement, Statement> updated_statements, int tacCounter, Method old_target)
            : base(md, tac_call)
        {
            this.tac = tac;
            this.tac_body = new List<Statement>(tac_body.ToArray());

            List<IVariable> lv_keys = new List<IVariable>(local_variables.Keys);
            List<object> lv_values = new List<object>(local_variables.Values);
            this.local_variables = lv_keys.ToDictionary(x => x, x => lv_values[lv_keys.IndexOf(x)]);

            List<Statement> us_keys = new List<Statement>(updated_statements.Keys);
            List<Statement> us_values = Util.Copy.CopyStatementList(new List<Statement>(updated_statements.Values));
            this.updated_statements = us_keys.ToDictionary(x => x, x => us_values[us_keys.IndexOf(x)]);
            this.tacCounter = tacCounter;
            if (old_target != null)
                this.new_target = new Method(old_target.tok, old_target.Name, old_target.HasStaticKeyword, old_target.IsGhost, old_target.TypeArgs,
                old_target.Ins, old_target.Outs, old_target.Req, old_target.Mod, old_target.Ens, old_target.Decreases, old_target.Body,
                old_target.Attributes, old_target.SignatureEllipsis);
        }

        public LocalContext Copy()
        {
            Method old_md = md as Method;
            return new LocalContext(
                new Method(old_md.tok, old_md.Name, old_md.HasStaticKeyword, old_md.IsGhost, old_md.TypeArgs,
                            old_md.Ins, old_md.Outs, old_md.Req, old_md.Mod, old_md.Ens,
                            new Specification<Expression>(new List<Expression>(old_md.Decreases.Expressions.ToArray()), old_md.Decreases.Attributes),
                            old_md.Body, old_md.Attributes, old_md.SignatureEllipsis),
                new Tactic(tac.tok, tac.Name, tac.HasStaticKeyword,
                            tac.TypeArgs, tac.Ins, tac.Outs, tac.Req,
                            tac.Mod, tac.Ens, tac.Decreases, tac.Body,
                            tac.Attributes, tac.SignatureEllipsis),
                tac_call, tac_body, local_variables, updated_statements, tacCounter, new_target);
        }


        /// <summary>
        /// Clear local variables, and fill them with tactic arguments. Use with caution.
        /// </summary>
        public void FillTacticInputs()
        {
            local_variables.Clear();
            ExprRhs er = (ExprRhs)tac_call.Rhss[0];
            List<Expression> exps = ((ApplySuffix)er.Expr).Args;
            Contract.Assert(exps.Count == tac.Ins.Count);
            for (int i = 0; i < exps.Count; i++)
                local_variables.Add(tac.Ins[i], exps[i]);
        }

        public bool HasLocalWithName(NameSegment ns)
        {
            if (ns == null)
                return false;
            List<Dafny.IVariable> ins = new List<Dafny.IVariable>(local_variables.Keys);
            foreach (Dafny.IVariable lv in ins)
                if (lv.Name == ns.Name)
                    return true;
            return false;
        }

        public object GetLocalValueByName(NameSegment ns)
        {
            Contract.Requires(ns != null);
            return GetLocalValueByName(ns.Name);
        }

        public object GetLocalValueByName(IVariable ns)
        {
            Contract.Requires(ns != null);
            return GetLocalValueByName(ns.Name);
        }

        public object GetLocalValueByName(string name)
        {
            Contract.Requires(name != null || name != "");
            List<Dafny.IVariable> ins = new List<Dafny.IVariable>(local_variables.Keys);
            foreach (Dafny.IVariable lv in ins)
                if (lv.Name == name)
                    return local_variables[lv];

            return null;
        }

        public IVariable GetLocalKeyByName(IVariable ns)
        {
            Contract.Requires(ns != null);
            return GetLocalKeyByName(ns.Name);
        }

        public IVariable GetLocalKeyByName(string name)
        {
            Contract.Requires(name != null);
            List<Dafny.IVariable> ins = new List<Dafny.IVariable>(local_variables.Keys);
            foreach (Dafny.IVariable lv in ins)
                if (lv.DisplayName == name)
                    return lv;
            return null;
        }

        public void AddLocal(IVariable lv, object value)
        {
            if (!local_variables.ContainsKey(lv))
                local_variables.Add(lv, value);
            else
                local_variables[lv] = value;
        }



        public void IncCounter()
        {
            if (tacCounter < tac_body.Count)
                tacCounter++;
            else
                throw new ArgumentOutOfRangeException("Tactic counter exceeded tactic body length");
        }

        public void DecCounter()
        {
            tacCounter--;
        }

        public int GetCounter()
        {
            return tacCounter;
        }

        public Statement GetCurrentStatement()
        {
            if (tacCounter >= tac_body.Count)
                return null;
            return tac_body[tacCounter];
        }

        public Statement GetNextStatement()
        {
            if (tacCounter + 1 >= tac_body.Count)
                return null;
            return tac_body[tacCounter+1];
        }


        public bool IsResolved()
        {
            return tacCounter == tac.Body.Body.Count;
        }

        public void AddUpdated(Statement key, Statement value)
        {
            if (!updated_statements.ContainsKey(key))
                updated_statements.Add(key, value);
            else
                updated_statements[key] = value;
        }

        public void RemoveUpdated(Statement key)
        {
            if (updated_statements.ContainsKey(key))
                updated_statements.Remove(key);
        }

        public Statement GetUpdated(Statement key)
        {
            if (updated_statements.ContainsKey(key))
                return updated_statements[key];
            return null;
        }

        public List<Statement> GetAllUpdated()
        {
            return new List<Statement>(updated_statements.Values.ToArray());
        }

        public List<Statement> GetFreshTacticBody()
        {
            return new List<Statement>(tac.Body.Body.ToArray());
        }

        public Method GetSourceMethod()
        {
            return md as Method;
        }
    }
    #endregion

    #region GlobalContext

    public class GlobalContext : Context
    {
        protected readonly Dictionary<string, DatatypeDecl> datatypes = new Dictionary<string, DatatypeDecl>();
        public Dictionary<string, IVariable> global_variables = new Dictionary<string, IVariable>();
        public Dictionary<string, IVariable> temp_variables = new Dictionary<string, IVariable>();
        public List<Statement> resolved = new List<Statement>();
        public Method new_target = null;
        public int total_branch_count = 0;
        public int bad_branch_count = 0;
        public Program program;


        public GlobalContext(MemberDecl md, UpdateStmt tac_call, Program program)
            : base(md, tac_call)
        {
            this.program = program;
            foreach (DatatypeDecl tld in program.globals)
                this.datatypes.Add(tld.Name, tld);

        }

        public bool ContainsGlobalKey(string name)
        {
            return datatypes.ContainsKey(name);
        }

        public DatatypeDecl GetGlobal(string name)
        {
            return datatypes[name];
        }

        public void RegsiterGlobalVariables(List<IVariable> globals)
        {
            foreach (var item in globals)
            {
                if (!global_variables.ContainsKey(item.Name))
                    global_variables.Add(item.Name, item);
                else
                    global_variables[item.Name] = item;
            }
        }

        public void ClearGlobalVariables()
        {
            global_variables.Clear();
        }

        public bool HasGlobalVariable(string name)
        {
            return global_variables.ContainsKey(name);
        }

        public IVariable GetGlobalVariable(string name)
        {
            if (HasGlobalVariable(name))
                return global_variables[name];
            return null;
        }

        public void RegisterTempVariable(IVariable var)
        {
            if (!temp_variables.ContainsKey(var.Name))
                temp_variables.Add(var.Name, var);
            else
                temp_variables[var.Name] = var;
        }

        public void RemoveTempVariable(IVariable var)
        {
            if (temp_variables.ContainsKey(var.Name))
                temp_variables.Remove(var.Name);
        }

        public void IncTotalBranchCount(int count)
        {
            total_branch_count += count;
        }

        public int GetTotalBranchCount()
        {
            return total_branch_count;
        }

        public void IncBadBranchCount(int count)
        {
           bad_branch_count += count;
        }

        public int GetBadBranchCount()
        {
            return bad_branch_count;
        }
    }
    #endregion
}
