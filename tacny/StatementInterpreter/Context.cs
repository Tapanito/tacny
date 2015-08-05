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
        public readonly MemberDecl md;
        public readonly UpdateStmt tac_call;

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
    public class LocalContext : Context
    {
        public readonly Tactic tac = null;  // The called tactic
        public List<Statement> tac_body = new List<Statement>(); // body of the currently worked tactic
        public Dictionary<Dafny.IVariable, object> local_variables = new Dictionary<Dafny.IVariable, object>();
        public Dictionary<Statement, Statement> updated_statements = new Dictionary<Statement, Statement>();
        public List<Statement> resolved = new List<Statement>();

        public LocalContext(MemberDecl md, Tactic tac, UpdateStmt tac_call)
            : base(md, tac_call)
        {
            this.tac = tac;
            this.tac_body = new List<Statement>(tac.Body.Body.ToArray());
            FillTacticInputs();
        }

        public LocalContext(MemberDecl md, Tactic tac, UpdateStmt tac_call, List<Statement> tac_body, Dictionary<Dafny.IVariable, object> local_variables,
            Dictionary<Statement, Statement> updated_statements, List<Statement> resolved)
            : base(md, tac_call)
        {
            this.tac = tac;
            this.tac_body = new List<Statement>(tac_body.ToArray());

            List<IVariable> lv_keys = new List<IVariable>(local_variables.Keys);
            List<object> lv_values = new List<object>(local_variables.Values);
            this.local_variables = lv_keys.ToDictionary(x => x, x => lv_values[lv_keys.IndexOf(x)]);

            List<Statement> us_keys = new List<Statement>(updated_statements.Keys);
            List<Statement> us_values = new List<Statement>(updated_statements.Values);
            this.updated_statements = us_keys.ToDictionary(x => x, x => us_values[us_keys.IndexOf(x)]);

            this.resolved = new List<Statement>(resolved.ToArray());
        }

        public LocalContext Copy()
        {
            return new LocalContext(md,
                new Tactic(tac.tok, tac.Name, tac.HasStaticKeyword,
                            tac.TypeArgs, tac.Ins, tac.Outs, tac.Req,
                            tac.Mod, tac.Ens, tac.Decreases, tac.Body,
                            tac.Attributes, tac.SignatureEllipsis),
                tac_call, tac_body, local_variables, updated_statements, resolved);
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

        public void Fin()
        {
            // for now try to copy dict values
            Statement[] tmp = (Statement[])updated_statements.Values.ToArray().Clone();
            resolved = new List<Statement>(tmp);
        }
    }

    public class GlobalContext : Context
    {
        protected readonly Dictionary<string, DatatypeDecl> global_variables = new Dictionary<string, DatatypeDecl>();
        public Program program;


        public GlobalContext(MemberDecl md, UpdateStmt tac_call, Program program)
            : base(md, tac_call)
        {
            this.program = program;
            foreach (DatatypeDecl tld in program.globals)
                this.global_variables.Add(tld.Name, tld);
        }

        public bool ContainsGlobalKey(string name)
        {
            return global_variables.ContainsKey(name);
        }

        public DatatypeDecl GetGlobal(string name)
        {
            return global_variables[name];
        }
    }
}