//
// codegen.cs: The code generator
//
// Author:
//   Miguel de Icaza (miguel@ximian.com)
//
// (C) 2001, 2002, 2003 Ximian, Inc.
// (C) 2004 Novell, Inc.
//

#if !DEBUG
	#define PRODUCTION
#endif

using System;
using System.IO;
using System.Collections;
using System.Collections.Specialized;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Permissions;

using Mono.Security.Cryptography;

namespace Mono.CSharp {

	/// <summary>
	///    Code generator class.
	/// </summary>
	public class CodeGen {
		static AppDomain current_domain;
		static public SymbolWriter SymbolWriter;

		public static AssemblyClass Assembly;
		public static ModuleClass Module;

		static CodeGen ()
		{
			Reset ();
		}

		public static void Reset ()
		{
			Assembly = new AssemblyClass ();
			Module = new ModuleClass (RootContext.Unsafe);
		}

		public static string Basename (string name)
		{
			int pos = name.LastIndexOf ('/');

			if (pos != -1)
				return name.Substring (pos + 1);

			pos = name.LastIndexOf ('\\');
			if (pos != -1)
				return name.Substring (pos + 1);

			return name;
		}

		public static string Dirname (string name)
		{
			int pos = name.LastIndexOf ('/');

			if (pos != -1)
				return name.Substring (0, pos);

			pos = name.LastIndexOf ('\\');
			if (pos != -1)
				return name.Substring (0, pos);

			return ".";
		}

		static public string FileName;

		//
		// Initializes the symbol writer
		//
		static void InitializeSymbolWriter (string filename)
		{
			SymbolWriter = SymbolWriter.GetSymbolWriter (Module.Builder, filename);

			//
			// If we got an ISymbolWriter instance, initialize it.
			//
			if (SymbolWriter == null) {
				Report.Warning (
					-18, 1, "Could not find the symbol writer assembly (Mono.CompilerServices.SymbolWriter.dll). This is normally an installation problem. Please make sure to compile and install the mcs/class/Mono.CompilerServices.SymbolWriter directory.");
				return;
			}
		}

		//
		// Initializes the code generator variables
		//
		static public bool Init (string name, string output, bool want_debugging_support)
		{
			FileName = output;
			AssemblyName an = Assembly.GetAssemblyName (name, output);
			if (an == null)
				return false;

			if (an.KeyPair != null) {
				// If we are going to strong name our assembly make
				// sure all its refs are strong named
				foreach (Assembly a in RootNamespace.Global.Assemblies) {
					AssemblyName ref_name = a.GetName ();
					byte [] b = ref_name.GetPublicKeyToken ();
					if (b == null || b.Length == 0) {
						Report.Error (1577, "Assembly generation failed " +
								"-- Referenced assembly '" +
								ref_name.Name +
								"' does not have a strong name.");
						//Environment.Exit (1);
					}
				}
			}
			
			current_domain = AppDomain.CurrentDomain;

			try {
				Assembly.Builder = current_domain.DefineDynamicAssembly (an,
					AssemblyBuilderAccess.Save, Dirname (name));
			}
			catch (ArgumentException) {
				// specified key may not be exportable outside it's container
				if (RootContext.StrongNameKeyContainer != null) {
					Report.Error (1548, "Could not access the key inside the container `" +
						RootContext.StrongNameKeyContainer + "'.");
					Environment.Exit (1);
				}
				return false;
			}
			catch (CryptographicException) {
				if ((RootContext.StrongNameKeyContainer != null) || (RootContext.StrongNameKeyFile != null)) {
					Report.Error (1548, "Could not use the specified key to strongname the assembly.");
					Environment.Exit (1);
				}
				return false;
			}

			//
			// Pass a path-less name to DefineDynamicModule.  Wonder how
			// this copes with output in different directories then.
			// FIXME: figure out how this copes with --output /tmp/blah
			//
			// If the third argument is true, the ModuleBuilder will dynamically
			// load the default symbol writer.
			//
			Module.Builder = Assembly.Builder.DefineDynamicModule (
				Basename (name), Basename (output), false);

			if (want_debugging_support)
				InitializeSymbolWriter (output);

			return true;
		}

		static public void Save (string name)
		{
			try {
				Assembly.Builder.Save (Basename (name));
			}
			catch (COMException) {
				if ((RootContext.StrongNameKeyFile == null) || (!RootContext.StrongNameDelaySign))
					throw;

				// FIXME: it seems Microsoft AssemblyBuilder doesn't like to delay sign assemblies 
				Report.Error (1548, "Couldn't delay-sign the assembly with the '" +
					RootContext.StrongNameKeyFile +
					"', Use MCS with the Mono runtime or CSC to compile this assembly.");
			}
			catch (System.IO.IOException io) {
				Report.Error (16, "Could not write to file `"+name+"', cause: " + io.Message);
			}
			catch (System.UnauthorizedAccessException ua) {
				Report.Error (16, "Could not write to file `"+name+"', cause: " + ua.Message);
			}

			if (SymbolWriter != null)
				SymbolWriter.WriteSymbolFile ();
		}
	}

	/// <summary>
	///   An Emit Context is created for each body of code (from methods,
	///   properties bodies, indexer bodies or constructor bodies)
	/// </summary>
	public class EmitContext {
		public DeclSpace DeclSpace;
		public DeclSpace TypeContainer;
		public ILGenerator   ig;

		/// <summary>
		///   This variable tracks the `checked' state of the compilation,
		///   it controls whether we should generate code that does overflow
		///   checking, or if we generate code that ignores overflows.
		///
		///   The default setting comes from the command line option to generate
		///   checked or unchecked code plus any source code changes using the
		///   checked/unchecked statements or expressions.   Contrast this with
		///   the ConstantCheckState flag.
		/// </summary>
		
		public bool CheckState;

		/// <summary>
		///   The constant check state is always set to `true' and cant be changed
		///   from the command line.  The source code can change this setting with
		///   the `checked' and `unchecked' statements and expressions. 
		/// </summary>
		public bool ConstantCheckState;

		/// <summary>
		///   Whether we are emitting code inside a static or instance method
		/// </summary>
		public bool IsStatic;

		/// <summary>
		///   Whether the actual created method is static or instance method.
		///   Althoug the method might be declared as `static', if an anonymous
		///   method is involved, we might turn this into an instance method.
		///
		///   So this reflects the low-level staticness of the method, while
		///   IsStatic represents the semantic, high-level staticness.
		/// </summary>
		public bool MethodIsStatic;

		/// <summary>
		///   Whether we are emitting a field initializer
		/// </summary>
		public bool IsFieldInitializer;

		/// <summary>
		///   The value that is allowed to be returned or NULL if there is no
		///   return type.
		/// </summary>
		public Type ReturnType;

		/// <summary>
		///   Points to the Type (extracted from the TypeContainer) that
		///   declares this body of code
		/// </summary>
		public Type ContainerType;
		
		/// <summary>
		///   Whether this is generating code for a constructor
		/// </summary>
		public bool IsConstructor;

		/// <summary>
		///   Whether we're control flow analysis enabled
		/// </summary>
		public bool DoFlowAnalysis;

		/// <summary>
		///   Whether we're control flow analysis disabled on struct
		/// </summary>
		public bool OmitStructFlowAnalysis;

		/// <summary>
		///   Keeps track of the Type to LocalBuilder temporary storage created
		///   to store structures (used to compute the address of the structure
		///   value on structure method invocations)
		/// </summary>
		public Hashtable temporary_storage;

		public Block CurrentBlock;

		public int CurrentFile;

		/// <summary>
		///   The location where we store the return value.
		/// </summary>
		LocalBuilder return_value;

		/// <summary>
		///   The location where return has to jump to return the
		///   value
		/// </summary>
		public Label ReturnLabel;

		/// <summary>
		///   If we already defined the ReturnLabel
		/// </summary>
		public bool HasReturnLabel;

		/// <summary>
		///   Whether we are inside an iterator block.
		/// </summary>
		public bool InIterator;

		public bool IsLastStatement;

		/// <summary>
		///  Whether we are inside an unsafe block
		/// </summary>
		public bool InUnsafe;

		/// <summary>
		///  Whether we are in a `fixed' initialization
		/// </summary>
		public bool InFixedInitializer;

		public bool InRefOutArgumentResolving;

		public bool InCatch;
		public bool InFinally;

		/// <summary>
		///  Whether we are inside an anonymous method.
		/// </summary>
		public AnonymousContainer CurrentAnonymousMethod;
		
		/// <summary>
		///   Location for this EmitContext
		/// </summary>
		public Location loc;

		/// <summary>
		///   Inside an enum definition, we do not resolve enumeration values
		///   to their enumerations, but rather to the underlying type/value
		///   This is so EnumVal + EnumValB can be evaluated.
		///
		///   There is no "E operator + (E x, E y)", so during an enum evaluation
		///   we relax the rules
		/// </summary>
		public bool InEnumContext;

		/// <summary>
		///   Anonymous methods can capture local variables and fields,
		///   this object tracks it.  It is copied from the TopLevelBlock
		///   field.
		/// </summary>
		public CaptureContext capture_context;

		/// <summary>
		/// Trace when method is called and is obsolete then this member suppress message
		/// when call is inside next [Obsolete] method or type.
		/// </summary>
		public bool TestObsoleteMethodUsage = true;

		/// <summary>
		///    The current iterator
		/// </summary>
		public Iterator CurrentIterator;

		/// <summary>
		///    Whether we are in the resolving stage or not
		/// </summary>
		enum Phase {
			Created,
			Resolving,
			Emitting
		}
		
		Phase current_phase;
		FlowBranching current_flow_branching;

		static int next_id = 0;
		int id = ++next_id;

		public override string ToString ()
		{
			return String.Format ("EmitContext ({0}:{1}:{2})", id,
					      CurrentIterator, capture_context, loc);
		}
		
		public EmitContext (DeclSpace parent, DeclSpace ds, Location l, ILGenerator ig,
				    Type return_type, int code_flags, bool is_constructor)
		{
			this.ig = ig;

			TypeContainer = parent;
			DeclSpace = ds;
			CheckState = RootContext.Checked;
			ConstantCheckState = true;

			IsStatic = (code_flags & Modifiers.STATIC) != 0;
			MethodIsStatic = IsStatic;
			InIterator = (code_flags & Modifiers.METHOD_YIELDS) != 0;
			ReturnType = return_type;
			IsConstructor = is_constructor;
			CurrentBlock = null;
			CurrentFile = 0;
			current_phase = Phase.Created;
			
			if (parent != null){
				// Can only be null for the ResolveType contexts.
				ContainerType = parent.TypeBuilder;
				if (parent.UnsafeContext)
					InUnsafe = true;
				else
					InUnsafe = (code_flags & Modifiers.UNSAFE) != 0;
			}
			loc = l;

			if (ReturnType == TypeManager.void_type)
				ReturnType = null;
		}

		public EmitContext (TypeContainer tc, Location l, ILGenerator ig,
				    Type return_type, int code_flags, bool is_constructor)
			: this (tc, tc, l, ig, return_type, code_flags, is_constructor)
		{
		}

		public EmitContext (TypeContainer tc, Location l, ILGenerator ig,
				    Type return_type, int code_flags)
			: this (tc, tc, l, ig, return_type, code_flags, false)
		{
		}

		public FlowBranching CurrentBranching {
			get {
				return current_flow_branching;
			}
		}

		public bool HaveCaptureInfo {
			get {
				return capture_context != null;
			}
		}

		// <summary>
		//   Starts a new code branching.  This inherits the state of all local
		//   variables and parameters from the current branching.
		// </summary>
		public FlowBranching StartFlowBranching (FlowBranching.BranchingType type, Location loc)
		{
			current_flow_branching = FlowBranching.CreateBranching (CurrentBranching, type, null, loc);
			return current_flow_branching;
		}

		// <summary>
		//   Starts a new code branching for block `block'.
		// </summary>
		public FlowBranching StartFlowBranching (Block block)
		{
			FlowBranching.BranchingType type;

			if ((CurrentBranching != null) &&
			    (CurrentBranching.Type == FlowBranching.BranchingType.Switch))
				type = FlowBranching.BranchingType.SwitchSection;
			else
				type = FlowBranching.BranchingType.Block;

			DoFlowAnalysis = true;

			current_flow_branching = FlowBranching.CreateBranching (
				CurrentBranching, type, block, block.StartLocation);
			return current_flow_branching;
		}

		public FlowBranchingException StartFlowBranching (ExceptionStatement stmt)
		{
			FlowBranchingException branching = new FlowBranchingException (
				CurrentBranching, stmt);
			current_flow_branching = branching;
			return branching;
		}

		// <summary>
		//   Ends a code branching.  Merges the state of locals and parameters
		//   from all the children of the ending branching.
		// </summary>
		public FlowBranching.UsageVector DoEndFlowBranching ()
		{
			FlowBranching old = current_flow_branching;
			current_flow_branching = current_flow_branching.Parent;

			return current_flow_branching.MergeChild (old);
		}

		// <summary>
		//   Ends a code branching.  Merges the state of locals and parameters
		//   from all the children of the ending branching.
		// </summary>
		public FlowBranching.Reachability EndFlowBranching ()
		{
			FlowBranching.UsageVector vector = DoEndFlowBranching ();

			return vector.Reachability;
		}

		// <summary>
		//   Kills the current code branching.  This throws away any changed state
		//   information and should only be used in case of an error.
		// </summary>
		public void KillFlowBranching ()
		{
			current_flow_branching = current_flow_branching.Parent;
		}

		public void CaptureVariable (LocalInfo li)
		{
			capture_context.AddLocal (CurrentAnonymousMethod, li);
			li.IsCaptured = true;
		}

		public void CaptureParameter (string name, Type t, int idx)
		{
			capture_context.AddParameter (this, CurrentAnonymousMethod, name, t, idx);
		}

		public void CaptureThis ()
		{
			capture_context.CaptureThis (CurrentAnonymousMethod);
		}
		
		
		//
		// Use to register a field as captured
		//
		public void CaptureField (FieldExpr fe)
		{
			capture_context.AddField (this, CurrentAnonymousMethod, fe);
		}

		//
		// Whether anonymous methods have captured variables
		//
		public bool HaveCapturedVariables ()
		{
			if (capture_context != null)
				return capture_context.HaveCapturedVariables;
			return false;
		}

		//
		// Whether anonymous methods have captured fields or this.
		//
		public bool HaveCapturedFields ()
		{
			if (capture_context != null)
				return capture_context.HaveCapturedFields;
			return false;
		}

		//
		// Emits the instance pointer for the host method
		//
		public void EmitMethodHostInstance (EmitContext target, AnonymousMethod am)
		{
			if (capture_context != null)
				capture_context.EmitMethodHostInstance (target, am);
			else if (IsStatic)
				target.ig.Emit (OpCodes.Ldnull);
			else
				target.ig.Emit (OpCodes.Ldarg_0);
		}

		//
		// Returns whether the `local' variable has been captured by an anonymous
		// method
		//
		public bool IsCaptured (LocalInfo local)
		{
			return capture_context.IsCaptured (local);
		}

		public bool IsParameterCaptured (string name)
		{
			if (capture_context != null)
				return capture_context.IsParameterCaptured (name);
			return false;
		}
		
		public void EmitMeta (ToplevelBlock b)
		{
			if (capture_context != null)
				capture_context.EmitAnonymousHelperClasses (this);
			b.EmitMeta (this);

			if (HasReturnLabel)
				ReturnLabel = ig.DefineLabel ();
		}

		//
		// Here until we can fix the problem with Mono.CSharp.Switch, which
		// currently can not cope with ig == null during resolve (which must
		// be fixed for switch statements to work on anonymous methods).
		//
		public void EmitTopBlock (IMethodData md, ToplevelBlock block)
		{
			if (block == null)
				return;
			
			bool unreachable;
			
			if (ResolveTopBlock (null, block, md.ParameterInfo, md, out unreachable)){
				EmitMeta (block);

				current_phase = Phase.Emitting;
				EmitResolvedTopBlock (block, unreachable);
			}
		}

		bool resolved;

		public bool ResolveTopBlock (EmitContext anonymous_method_host, ToplevelBlock block,
					     Parameters ip, IMethodData md, out bool unreachable)
		{
			current_phase = Phase.Resolving;
			
			unreachable = false;

			if (resolved)
				return true;

			capture_context = block.CaptureContext;
			
			if (!loc.IsNull)
				CurrentFile = loc.File;

#if PRODUCTION
			try {
#endif
				if (!block.ResolveMeta (this, ip))
					return false;

				bool old_do_flow_analysis = DoFlowAnalysis;
				DoFlowAnalysis = true;

				if (anonymous_method_host != null)
					current_flow_branching = FlowBranching.CreateBranching (
						anonymous_method_host.CurrentBranching,
						FlowBranching.BranchingType.Block, block, loc);
				else 
					current_flow_branching = block.TopLevelBranching;

				if (!block.Resolve (this)) {
					current_flow_branching = null;
					DoFlowAnalysis = old_do_flow_analysis;
					return false;
				}

				FlowBranching.Reachability reachability = current_flow_branching.MergeTopBlock ();
				current_flow_branching = null;

				DoFlowAnalysis = old_do_flow_analysis;

				if (reachability.AlwaysReturns ||
				    reachability.AlwaysThrows ||
				    reachability.IsUnreachable)
					unreachable = true;
#if PRODUCTION
			} catch (Exception e) {
					Console.WriteLine ("Exception caught by the compiler while compiling:");
					Console.WriteLine ("   Block that caused the problem begin at: " + loc);
					
					if (CurrentBlock != null){
						Console.WriteLine ("                     Block being compiled: [{0},{1}]",
								   CurrentBlock.StartLocation, CurrentBlock.EndLocation);
					}
					Console.WriteLine (e.GetType ().FullName + ": " + e.Message);
					throw;
			}
#endif

			if (ReturnType != null && !unreachable) {
				if (CurrentAnonymousMethod == null) {
					Report.Error (161, md.Location, "`{0}': not all code paths return a value", md.GetSignatureForError ());
					return false;
				} else if (!CurrentAnonymousMethod.IsIterator) {
					Report.Error (1643, CurrentAnonymousMethod.Location, "Not all code paths return a value in anonymous method of type `{0}'",
						CurrentAnonymousMethod.GetSignatureForError ());
					return false;
				}
			}

			block.CompleteContexts ();
			resolved = true;
			return true;
		}

		public void EmitResolvedTopBlock (ToplevelBlock block, bool unreachable)
		{
			if (block != null)
				block.Emit (this);
			
			if (HasReturnLabel)
				ig.MarkLabel (ReturnLabel);
			
			if (return_value != null){
				ig.Emit (OpCodes.Ldloc, return_value);
				ig.Emit (OpCodes.Ret);
			} else {
				//
				// If `HasReturnLabel' is set, then we already emitted a
				// jump to the end of the method, so we must emit a `ret'
				// there.
				//
				// Unfortunately, System.Reflection.Emit automatically emits
				// a leave to the end of a finally block.  This is a problem
				// if no code is following the try/finally block since we may
				// jump to a point after the end of the method.
				// As a workaround, we're always creating a return label in
				// this case.
				//

				bool in_iterator = (CurrentAnonymousMethod != null) &&
					CurrentAnonymousMethod.IsIterator && InIterator;

				if ((block != null) && block.IsDestructor) {
					// Nothing to do; S.R.E automatically emits a leave.
				} else if (HasReturnLabel || (!unreachable && !in_iterator)) {
					if (ReturnType != null)
						ig.Emit (OpCodes.Ldloc, TemporaryReturn ());
					ig.Emit (OpCodes.Ret);
				}
			}

			//
			// Close pending helper classes if we are the toplevel
			//
			if (capture_context != null && capture_context.ParentToplevel == null)
				capture_context.CloseAnonymousHelperClasses ();
		}

		/// <summary>
		///   This is called immediately before emitting an IL opcode to tell the symbol
		///   writer to which source line this opcode belongs.
		/// </summary>
		public void Mark (Location loc, bool check_file)
		{
			if ((CodeGen.SymbolWriter == null) || loc.IsNull)
				return;

			if (check_file && (CurrentFile != loc.File))
				return;

			CodeGen.SymbolWriter.MarkSequencePoint (ig, loc.Row, loc.Column);
		}

		public void DefineLocalVariable (string name, LocalBuilder builder)
		{
			if (CodeGen.SymbolWriter == null)
				return;

			CodeGen.SymbolWriter.DefineLocalVariable (name, builder);
		}

		public void BeginScope ()
		{
			ig.BeginScope();

			if (CodeGen.SymbolWriter != null)
				CodeGen.SymbolWriter.OpenScope(ig);
		}

		public void EndScope ()
		{
			ig.EndScope();

			if (CodeGen.SymbolWriter != null)
				CodeGen.SymbolWriter.CloseScope(ig);
		}

		/// <summary>
		///   Returns a temporary storage for a variable of type t as 
		///   a local variable in the current body.
		/// </summary>
		public LocalBuilder GetTemporaryLocal (Type t)
		{
			LocalBuilder location = null;
			
			if (temporary_storage != null){
				object o = temporary_storage [t];
				if (o != null){
					if (o is ArrayList){
						ArrayList al = (ArrayList) o;
						
						for (int i = 0; i < al.Count; i++){
							if (al [i] != null){
								location = (LocalBuilder) al [i];
								al [i] = null;
								break;
							}
						}
					} else
						location = (LocalBuilder) o;
					if (location != null)
						return location;
				}
			}
			
			return ig.DeclareLocal (t);
		}

		public void FreeTemporaryLocal (LocalBuilder b, Type t)
		{
			if (temporary_storage == null){
				temporary_storage = new Hashtable ();
				temporary_storage [t] = b;
				return;
			}
			object o = temporary_storage [t];
			if (o == null){
				temporary_storage [t] = b;
				return;
			}
			if (o is ArrayList){
				ArrayList al = (ArrayList) o;
				for (int i = 0; i < al.Count; i++){
					if (al [i] == null){
						al [i] = b;
						return;
					}
				}
				al.Add (b);
				return;
			}
			ArrayList replacement = new ArrayList ();
			replacement.Add (o);
			temporary_storage.Remove (t);
			temporary_storage [t] = replacement;
		}

		/// <summary>
		///   Current loop begin and end labels.
		/// </summary>
		public Label LoopBegin, LoopEnd;

		/// <summary>
		///   Default target in a switch statement.   Only valid if
		///   InSwitch is true
		/// </summary>
		public Label DefaultTarget;

		/// <summary>
		///   If this is non-null, points to the current switch statement
		/// </summary>
		public Switch Switch;

		/// <summary>
		///   ReturnValue creates on demand the LocalBuilder for the
		///   return value from the function.  By default this is not
		///   used.  This is only required when returns are found inside
		///   Try or Catch statements.
		///
		///   This method is typically invoked from the Emit phase, so
		///   we allow the creation of a return label if it was not
		///   requested during the resolution phase.   Could be cleaned
		///   up, but it would replicate a lot of logic in the Emit phase
		///   of the code that uses it.
		/// </summary>
		public LocalBuilder TemporaryReturn ()
		{
			if (return_value == null){
				return_value = ig.DeclareLocal (ReturnType);
				if (!HasReturnLabel){
					ReturnLabel = ig.DefineLabel ();
					HasReturnLabel = true;
				}
			}

			return return_value;
		}

		/// <summary>
		///   This method is used during the Resolution phase to flag the
		///   need to define the ReturnLabel
		/// </summary>
		public void NeedReturnLabel ()
		{
			if (current_phase != Phase.Resolving){
				//
				// The reason is that the `ReturnLabel' is declared between
				// resolution and emission
				// 
				throw new Exception ("NeedReturnLabel called from Emit phase, should only be called during Resolve");
			}
			
			if (!InIterator && !HasReturnLabel) 
				HasReturnLabel = true;
		}

		//
		// Emits the proper object to address fields on a remapped
		// variable/parameter to field in anonymous-method/iterator proxy classes.
		//
		public void EmitThis (bool need_address)
		{
			ig.Emit (OpCodes.Ldarg_0);
			if (capture_context != null && CurrentAnonymousMethod != null){
				ScopeInfo si = CurrentAnonymousMethod.Scope;
				while (si != null){
					if (si.ParentLink != null)
						ig.Emit (OpCodes.Ldfld, si.ParentLink);
					if (si.THIS != null){
						if (need_address && TypeManager.IsValueType (si.THIS.FieldType))
							ig.Emit (OpCodes.Ldflda, si.THIS);
						else
							ig.Emit (OpCodes.Ldfld, si.THIS);
						break;
					}
					si = si.ParentScope;
				}
			} 
		}

		//
		// Emits the code necessary to load the instance required
		// to access the captured LocalInfo
		//
		public void EmitCapturedVariableInstance (LocalInfo li)
		{
			if (capture_context == null)
				throw new Exception ("Calling EmitCapturedContext when there is no capture_context");
			
			capture_context.EmitCapturedVariableInstance (this, li, CurrentAnonymousMethod);
		}

		public void EmitParameter (string name, bool leave_copy, bool prepared, ref LocalTemporary temp)
		{
			capture_context.EmitParameter (this, name, leave_copy, prepared, ref temp);
		}

		public void EmitAssignParameter (string name, Expression source, bool leave_copy, bool prepare_for_load, ref LocalTemporary  temp)
		{
			capture_context.EmitAssignParameter (this, name, source, leave_copy, prepare_for_load, ref temp);
		}

		public void EmitAddressOfParameter (string name)
		{
			capture_context.EmitAddressOfParameter (this, name);
		}
		
		public Expression GetThis (Location loc)
		{
			This my_this;
			if (CurrentBlock != null)
				my_this = new This (CurrentBlock, loc);
			else
				my_this = new This (loc);

			if (!my_this.ResolveBase (this))
				my_this = null;

			return my_this;
		}
	}


	public abstract class CommonAssemblyModulClass : Attributable {
		protected CommonAssemblyModulClass ():
			base (null)
		{
		}

		public void AddAttributes (ArrayList attrs)
		{
			if (OptAttributes == null) {
				OptAttributes = new Attributes (attrs);
				return;
			}
			OptAttributes.AddAttributes (attrs);
		}

		public virtual void Emit (TypeContainer tc) 
		{
			if (OptAttributes == null)
				return;

			EmitContext ec = new EmitContext (tc, Mono.CSharp.Location.Null, null, null, 0, false);
			OptAttributes.Emit (ec, this);
		}
                
		protected Attribute ResolveAttribute (Type a_type)
		{
			if (OptAttributes == null)
				return null;

			// Ensure that we only have GlobalAttributes, since the Search below isn't safe with other types.
			if (!OptAttributes.CheckTargets (this))
				return null;

			EmitContext temp_ec = new EmitContext (RootContext.Tree.Types, Mono.CSharp.Location.Null, null, null, 0, false);
			Attribute a = OptAttributes.Search (a_type, temp_ec);
			if (a != null) {
				a.Resolve (temp_ec);
			}
			return a;
		}
	}
                
	public class AssemblyClass : CommonAssemblyModulClass {
		// TODO: make it private and move all builder based methods here
		public AssemblyBuilder Builder;
		bool is_cls_compliant;
		bool wrap_non_exception_throws;

		public Attribute ClsCompliantAttribute;

		ListDictionary declarative_security;

		// Module is here just because of error messages
		static string[] attribute_targets = new string [] { "assembly", "module" };

		public AssemblyClass (): base ()
		{
			wrap_non_exception_throws = true;
		}

		public bool IsClsCompliant {
			get {
				return is_cls_compliant;
			}
		}

		public bool WrapNonExceptionThrows {
			get {
				return wrap_non_exception_throws;
			}
		}

		public override AttributeTargets AttributeTargets {
			get {
				return AttributeTargets.Assembly;
			}
		}

		public override bool IsClsComplianceRequired(DeclSpace ds)
		{
			return is_cls_compliant;
		}

		public void Resolve ()
		{
			ClsCompliantAttribute = ResolveAttribute (TypeManager.cls_compliant_attribute_type);
			if (ClsCompliantAttribute != null) {
				is_cls_compliant = ClsCompliantAttribute.GetClsCompliantAttributeValue (null);
			}

#if NET_2_0
			Attribute a = ResolveAttribute (TypeManager.runtime_compatibility_attr_type);
			if (a != null) {
				object val = a.GetPropertyValue ("WrapNonExceptionThrows");
				if (val != null)
					wrap_non_exception_throws = (bool)val;
			}
#endif
		}

		// fix bug #56621
		private void SetPublicKey (AssemblyName an, byte[] strongNameBlob) 
		{
			try {
				// check for possible ECMA key
				if (strongNameBlob.Length == 16) {
					// will be rejected if not "the" ECMA key
					an.SetPublicKey (strongNameBlob);
				}
				else {
					// take it, with or without, a private key
					RSA rsa = CryptoConvert.FromCapiKeyBlob (strongNameBlob);
					// and make sure we only feed the public part to Sys.Ref
					byte[] publickey = CryptoConvert.ToCapiPublicKeyBlob (rsa);
					
					// AssemblyName.SetPublicKey requires an additional header
					byte[] publicKeyHeader = new byte [12] { 0x00, 0x24, 0x00, 0x00, 0x04, 0x80, 0x00, 0x00, 0x94, 0x00, 0x00, 0x00 };

					byte[] encodedPublicKey = new byte [12 + publickey.Length];
					Buffer.BlockCopy (publicKeyHeader, 0, encodedPublicKey, 0, 12);
					Buffer.BlockCopy (publickey, 0, encodedPublicKey, 12, publickey.Length);
					an.SetPublicKey (encodedPublicKey);
				}
			}
			catch (Exception) {
				Error_AssemblySigning ("The specified file `" + RootContext.StrongNameKeyFile + "' is incorrectly encoded");
				Environment.Exit (1);
			}
		}

		// TODO: rewrite this code (to kill N bugs and make it faster) and use standard ApplyAttribute way.
		public AssemblyName GetAssemblyName (string name, string output) 
		{
			if (OptAttributes != null) {
				foreach (Attribute a in OptAttributes.Attrs) {
					// cannot rely on any resolve-based members before you call Resolve
					if (a.ExplicitTarget == null || a.ExplicitTarget != "assembly")
						continue;

					// TODO: This code is buggy: comparing Attribute name without resolving is wrong.
					//       However, this is invoked by CodeGen.Init, when none of the namespaces
					//       are loaded yet.
					// TODO: Does not handle quoted attributes properly
					switch (a.Name) {
						case "AssemblyKeyFile":
						case "AssemblyKeyFileAttribute":
						case "System.Reflection.AssemblyKeyFileAttribute":
							if (RootContext.StrongNameKeyFile != null) {
								Report.SymbolRelatedToPreviousError (a.Location, a.Name);
								Report.Warning (1616, 1, "Option `{0}' overrides attribute `{1}' given in a source file or added module",
                                    "keyfile", "System.Reflection.AssemblyKeyFileAttribute");
							}
							else {
								string value = a.GetString ();
								if (value != String.Empty)
									RootContext.StrongNameKeyFile = value;
							}
							break;
						case "AssemblyKeyName":
						case "AssemblyKeyNameAttribute":
						case "System.Reflection.AssemblyKeyNameAttribute":
							if (RootContext.StrongNameKeyContainer != null) {
								Report.SymbolRelatedToPreviousError (a.Location, a.Name);
								Report.Warning (1616, 1, "Option `{0}' overrides attribute `{1}' given in a source file or added module",
									"keycontainer", "System.Reflection.AssemblyKeyNameAttribute");
							}
							else {
								string value = a.GetString ();
								if (value != String.Empty)
									RootContext.StrongNameKeyContainer = value;
							}
							break;
						case "AssemblyDelaySign":
						case "AssemblyDelaySignAttribute":
						case "System.Reflection.AssemblyDelaySignAttribute":
							RootContext.StrongNameDelaySign = a.GetBoolean ();
							break;
					}
				}
			}

			AssemblyName an = new AssemblyName ();
			an.Name = Path.GetFileNameWithoutExtension (name);

			// note: delay doesn't apply when using a key container
			if (RootContext.StrongNameKeyContainer != null) {
				an.KeyPair = new StrongNameKeyPair (RootContext.StrongNameKeyContainer);
				return an;
			}

			// strongname is optional
			if (RootContext.StrongNameKeyFile == null)
				return an;

			string AssemblyDir = Path.GetDirectoryName (output);

			// the StrongName key file may be relative to (a) the compiled
			// file or (b) to the output assembly. See bugzilla #55320
			// http://bugzilla.ximian.com/show_bug.cgi?id=55320

			// (a) relative to the compiled file
			string filename = Path.GetFullPath (RootContext.StrongNameKeyFile);
			bool exist = File.Exists (filename);
			if ((!exist) && (AssemblyDir != null) && (AssemblyDir != String.Empty)) {
				// (b) relative to the outputed assembly
				filename = Path.GetFullPath (Path.Combine (AssemblyDir, RootContext.StrongNameKeyFile));
				exist = File.Exists (filename);
			}

			if (exist) {
				using (FileStream fs = new FileStream (filename, FileMode.Open, FileAccess.Read)) {
					byte[] snkeypair = new byte [fs.Length];
					fs.Read (snkeypair, 0, snkeypair.Length);

					if (RootContext.StrongNameDelaySign) {
						// delayed signing - DO NOT include private key
						SetPublicKey (an, snkeypair);
					}
					else {
						// no delay so we make sure we have the private key
						try {
							CryptoConvert.FromCapiPrivateKeyBlob (snkeypair);
							an.KeyPair = new StrongNameKeyPair (snkeypair);
						}
						catch (CryptographicException) {
							if (snkeypair.Length == 16) {
								// error # is different for ECMA key
								Report.Error (1606, "Could not sign the assembly. " + 
									"ECMA key can only be used to delay-sign assemblies");
							}
							else {
								Error_AssemblySigning ("The specified file `" + RootContext.StrongNameKeyFile + "' does not have a private key");
							}
							return null;
						}
					}
				}
			}
			else {
				Error_AssemblySigning ("The specified file `" + RootContext.StrongNameKeyFile + "' does not exist");
				return null;
			}
			return an;
		}

		void Error_AssemblySigning (string text)
		{
			Report.Error (1548, "Error during assembly signing. " + text);
		}

		public override void ApplyAttributeBuilder (Attribute a, CustomAttributeBuilder customBuilder)
		{
			if (a.Type.IsSubclassOf (TypeManager.security_attr_type) && a.CheckSecurityActionValidity (true)) {
				if (declarative_security == null)
					declarative_security = new ListDictionary ();

				a.ExtractSecurityPermissionSet (declarative_security);
				return;
			}

			if (a.Type == TypeManager.assembly_culture_attribute_type) {
				string value = a.GetString ();
				if (value == null || value.Length == 0)
					return;

				if (RootContext.Target == Target.Exe) {
					a.Error_AttributeEmitError ("The executables cannot be satelite assemblies, remove the attribute or keep it empty");
					return;
				}
			}

			Builder.SetCustomAttribute (customBuilder);
		}

		public override void Emit (TypeContainer tc)
		{
			base.Emit (tc);

			if (declarative_security != null) {

				MethodInfo add_permission = typeof (AssemblyBuilder).GetMethod ("AddPermissionRequests", BindingFlags.Instance | BindingFlags.NonPublic);
				object builder_instance = Builder;

				try {
					// Microsoft runtime hacking
					if (add_permission == null) {
						Type assembly_builder = typeof (AssemblyBuilder).Assembly.GetType ("System.Reflection.Emit.AssemblyBuilderData");
						add_permission = assembly_builder.GetMethod ("AddPermissionRequests", BindingFlags.Instance | BindingFlags.NonPublic);

						FieldInfo fi = typeof (AssemblyBuilder).GetField ("m_assemblyData", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.GetField);
						builder_instance = fi.GetValue (Builder);
					}

					object[] args = new object [] { declarative_security [SecurityAction.RequestMinimum],
												  declarative_security [SecurityAction.RequestOptional],
												  declarative_security [SecurityAction.RequestRefuse] };
					add_permission.Invoke (builder_instance, args);
				}
				catch {
					Report.RuntimeMissingSupport (Location.Null, "assembly permission setting");
				}
			}

#if NET_2_0
			if (!OptAttributes.Contains (TypeManager.runtime_compatibility_attr_type, null)) {
				ConstructorInfo ci = TypeManager.runtime_compatibility_attr_type.GetConstructor (TypeManager.NoTypes);
				PropertyInfo pi = TypeManager.runtime_compatibility_attr_type.GetProperty ("WrapNonExceptionThrows");
				Builder.SetCustomAttribute (new CustomAttributeBuilder (ci, new object [0], 
					new PropertyInfo [] { pi }, new object[] { true } ));
			}
#endif
		}

		public override string[] ValidAttributeTargets {
			get {
				return attribute_targets;
			}
		}
	}

	public class ModuleClass : CommonAssemblyModulClass {
		// TODO: make it private and move all builder based methods here
		public ModuleBuilder Builder;
		bool m_module_is_unsafe;

		public CharSet DefaultCharSet = CharSet.Ansi;
		public TypeAttributes DefaultCharSetType = TypeAttributes.AnsiClass;

		static string[] attribute_targets = new string [] { "module" };

		public ModuleClass (bool is_unsafe)
		{
			m_module_is_unsafe = is_unsafe;
		}

 		public override AttributeTargets AttributeTargets {
 			get {
 				return AttributeTargets.Module;
 			}
		}

		public override bool IsClsComplianceRequired(DeclSpace ds)
		{
			return CodeGen.Assembly.IsClsCompliant;
		}

		public override void Emit (TypeContainer tc) 
		{
			base.Emit (tc);

			if (!m_module_is_unsafe)
				return;

			if (TypeManager.unverifiable_code_ctor == null) {
				Console.WriteLine ("Internal error ! Cannot set unverifiable code attribute.");
				return;
			}
				
			Builder.SetCustomAttribute (new CustomAttributeBuilder (TypeManager.unverifiable_code_ctor, new object [0]));
		}
                
		public override void ApplyAttributeBuilder (Attribute a, CustomAttributeBuilder customBuilder)
		{
			if (a.Type == TypeManager.cls_compliant_attribute_type) {
				if (CodeGen.Assembly.ClsCompliantAttribute == null) {
					Report.Warning (3012, 1, a.Location, "You must specify the CLSCompliant attribute on the assembly, not the module, to enable CLS compliance checking");
				}
				else if (CodeGen.Assembly.IsClsCompliant != a.GetBoolean ()) {
					Report.SymbolRelatedToPreviousError (CodeGen.Assembly.ClsCompliantAttribute.Location, CodeGen.Assembly.ClsCompliantAttribute.GetSignatureForError ());
					Report.Error (3017, a.Location, "You cannot specify the CLSCompliant attribute on a module that differs from the CLSCompliant attribute on the assembly");
					return;
				}
			}

			Builder.SetCustomAttribute (customBuilder);
		}

		/// <summary>
		/// It is called very early therefore can resolve only predefined attributes
		/// </summary>
		public void ResolveAttributes ()
		{
#if NET_2_0
			Attribute a = ResolveAttribute (TypeManager.default_charset_type);
			if (a != null) {
				DefaultCharSet = a.GetCharSetValue ();
				switch (DefaultCharSet) {
					case CharSet.Ansi:
					case CharSet.None:
						break;
					case CharSet.Auto:
						DefaultCharSetType = TypeAttributes.AutoClass;
						break;
					case CharSet.Unicode:
						DefaultCharSetType = TypeAttributes.UnicodeClass;
						break;
					default:
						Report.Error (1724, a.Location, "Value specified for the argument to 'System.Runtime.InteropServices.DefaultCharSetAttribute' is not valid");
						break;
				}
			}
#endif
		}

		public override string[] ValidAttributeTargets {
			get {
				return attribute_targets;
			}
		}
	}
}
