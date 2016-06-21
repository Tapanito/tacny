﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Reflection;
using Microsoft.Dafny;
using Type = Microsoft.Dafny.Type;

namespace Tacny {
  public class Interpreter {
    private static Interpreter _i;
    private Stack<Dictionary<IVariable, Type>> _frame;

    private readonly ProofState _state;
    private readonly ErrorReporter _errorReporter;


    private Interpreter(Program program) {
      Contract.Requires(tcce.NonNull(program));
      // initialize state
      _errorReporter = new ConsoleErrorReporter();
      _state = new ProofState(program, _errorReporter);
      _frame = new Stack<Dictionary<IVariable, Type>>();
    }


    [ContractInvariantMethod]
    private void ObjectInvariant() {
      Contract.Invariant(tcce.NonNull(_state));
      Contract.Invariant(tcce.NonNull(_frame));
      Contract.Invariant(_errorReporter != null);
    }

    public static MemberDecl FindAndApplyTactic(Program program, MemberDecl target) {
      Contract.Requires(program != null);
      Contract.Requires(target != null);
      if (_i == null) {
        _i = new Interpreter(program);
      }
      return _i.FindTacticApplication(target);
    }


    private MemberDecl FindTacticApplication(MemberDecl target) {
      Contract.Requires(tcce.NonNull(target));
      // initialize new stack for variables
      _frame = new Stack<Dictionary<IVariable, Type>>();
      var method = target as Method;
      if (method != null) {
        _state.SetTopLevelClass(method.EnclosingClass?.Name);
        var dict = method.Ins.Concat(method.Outs)
          .ToDictionary<IVariable, IVariable, Type>(item => item, item => item.Type);
        _frame.Push(dict);
        SearchBlockStmt(method.Body);
        dict = _frame.Pop();
        // sanity check
        Contract.Assert(_frame.Count == 0);
      }
      return method;
    }

    // Find tactic application and resolve it
    private void SearchBlockStmt(BlockStmt body) {
      Contract.Requires(tcce.NonNull(body));

      var dict = new Dictionary<IVariable, Type>();
      for (var i = 0; i < body.Body.Count; i++) {
        var stmt = body.Body[i];
        if (stmt is VarDeclStmt) {
          var vds = stmt as VarDeclStmt;
          // register local variable declarations
          foreach (var local in vds.Locals) {
            try {
              dict.Add(local, local.Type);
            } catch (Exception e) {
              //TODO: some error handling when target is not resolved
              Console.Out.WriteLine(e.Message);
            }
          }
        } else if (stmt is IfStmt) {
          var ifStmt = stmt as IfStmt;
          _frame.Push(dict);
          SearchIfStmt(ifStmt);
          // remove the top _frame
          dict = _frame.Pop(); // BUG!!!
        } else if (stmt is WhileStmt) {
          var whileStmt = stmt as WhileStmt;
          _frame.Push(dict);
          SearchBlockStmt(whileStmt.Body);
          dict = _frame.Pop();
        } else if (stmt is UpdateStmt) {
          var us = stmt as UpdateStmt;
          if (_state.IsTacticCall(us)) {
            var list = StackToDict(_frame);
            var result = ApplyTopLevelTactic(list, us);
            body.Body.RemoveAt(i);
            body.Body.InsertRange(i, result.GetGeneratedCode());
          }
        }
      }
    }

    private void SearchIfStmt(IfStmt ifStmt) {
      Contract.Requires(tcce.NonNull(ifStmt));

      SearchBlockStmt(ifStmt.Thn);
      if (ifStmt.Els == null) return;
      var els = ifStmt.Els as BlockStmt;
      if (els != null) {
        SearchBlockStmt(els);
      } else if (ifStmt.Els is IfStmt) {
        SearchIfStmt((IfStmt)ifStmt.Els);
      }
    }

    private static Dictionary<IVariable, Type> StackToDict(Stack<Dictionary<IVariable, Type>> stack) {
      Contract.Requires(stack != null);
      Contract.Ensures(Contract.Result<Dictionary<IVariable, Type>>() != null);
      var dict = new Dictionary<IVariable, Type>();
      return stack.Aggregate(dict, (current, item) => current.Concat(item).ToDictionary(x => x.Key, x => x.Value));
    }

    private ProofState ApplyTopLevelTactic(Dictionary<IVariable, Type> variables, UpdateStmt tacticApplication) {
      Contract.Requires<ArgumentNullException>(tcce.NonNull(variables));
      Contract.Requires<ArgumentNullException>(tcce.NonNull(tacticApplication));
      // get the tactic
      _state.InitState(tacticApplication, variables);
      var search = new BaseSearchStrategy(_state.TacticInfo.SearchStrategy, true);
      return search.Search(_state).FirstOrDefault();
    }

    public static void PrepareFrame(BlockStmt body, ProofState state) {
      Contract.Requires<ArgumentNullException>(body != null, "body");
      Contract.Requires<ArgumentNullException>(state != null, "state");
      state.AddNewFrame(body);
      // call the search engine
      var search = new BaseSearchStrategy(state.TacticInfo.SearchStrategy, true);
      search.Search(state);
      state.RemoveFrame();
    }

    public static IEnumerable<ProofState> EvalStep(ProofState state) {
      Contract.Requires<ArgumentNullException>(state != null, "state");
      // one step
      // call the 
      var stmt = state.GetStmt();
      if (stmt is TacticVarDeclStmt) {
        return RegisterVariable(stmt as TacticVarDeclStmt, state);
      } else if (stmt is UpdateStmt) {
        var us = stmt as UpdateStmt;
        if (state.IsLocalAssignment(us)) {
          return UpdateLocalValue(us, state);
        }
      }

      return DefaultAction(stmt, state);
    }


    private static IEnumerable<object> EvaluateTacnyExpression(ProofState state, ApplySuffix aps) {
      Contract.Requires<ArgumentNullException>(state != null, "state");
      Contract.Requires<ArgumentNullException>(aps != null, "aps");

      string sig = ProofState.GetSignature(aps);
      // using reflection find all classes that extend EAtomic
      var types =
        Assembly.GetAssembly(typeof(EAtomic.EAtomic)).GetTypes().Where(t => t.IsSubclassOf(typeof(EAtomic.EAtomic)));
      foreach (var type in types) {
        var resolverInstance = Activator.CreateInstance(type) as EAtomic.EAtomic;
        string resolverSig = resolverInstance?.Signature;
        if (sig == resolverSig) {
          //TODO: validate ins
          return resolverInstance?.Generate(aps, state);
        }
      }

      return null;
    }


    private static IEnumerable<ProofState> RegisterVariable(TacticVarDeclStmt declaration, ProofState state) {
      if (declaration.Update == null) yield break;
      var rhs = declaration.Update as UpdateStmt;
      if (rhs == null) {
        foreach (var item in declaration.Locals)
          state.AddLocal(item, null);
      } else {
        foreach (var item in rhs.Rhss) {
          int index = rhs.Rhss.IndexOf(item);
          Contract.Assert(declaration.Locals.ElementAtOrDefault(index) != null, "register var err");
          var exprRhs = item as ExprRhs;
          if (exprRhs?.Expr is ApplySuffix) {
            var aps = (ApplySuffix)exprRhs.Expr;
            foreach (var result in EvaluateTacnyExpression(state, aps)) {
              state.AddLocal(declaration.Locals[index], result);
            }
          } else if (exprRhs?.Expr is LiteralExpr) {
            state.AddLocal(declaration.Locals[index], (LiteralExpr)exprRhs?.Expr);
          } else {
            state.AddLocal(declaration.Locals[index], exprRhs?.Expr);
          }
        }
      }
      yield return state.Copy<ProofState>();
    }

    private static IEnumerable<ProofState> UpdateLocalValue(UpdateStmt us, ProofState state) {
      Contract.Requires<ArgumentNullException>(us != null, "stmt");
      Contract.Requires<ArgumentNullException>(state != null, "state");
      Contract.Requires<ArgumentException>(state.IsLocalAssignment(us), "stmt");

      foreach (var item in us.Rhss) {
        int index = us.Rhss.IndexOf(item);
        Contract.Assert(us.Lhss.ElementAtOrDefault(index) != null, "register var err");
        var exprRhs = item as ExprRhs;
        if (exprRhs?.Expr is ApplySuffix) {
          var aps = (ApplySuffix)exprRhs.Expr;
          foreach (var result in EvaluateTacnyExpression(state, aps)) {
            state.UpdateVariable(((NameSegment)us.Lhss[index]).Name, result);
          }
        } else if (exprRhs?.Expr is LiteralExpr) {
          state.UpdateVariable(((NameSegment)us.Lhss[index]).Name, (LiteralExpr) exprRhs?.Expr);
        } else {
          state.UpdateVariable(((NameSegment)us.Lhss[index]).Name, exprRhs?.Expr);
        }
      }
      yield return state.Copy();
    }

    /// <summary>
    /// Insert the statement as is into the state
    /// </summary>
    /// <param name="stmt"></param>
    /// <param name="state"></param>
    /// <returns></returns>
    private static IEnumerable<ProofState> DefaultAction(Statement stmt, ProofState state) {
      Contract.Requires<ArgumentNullException>(stmt != null, "stmt");
      Contract.Requires<ArgumentNullException>(state != null, "state");
      state.AddStatement(stmt);
      yield return state.Copy();
    }
  }
}