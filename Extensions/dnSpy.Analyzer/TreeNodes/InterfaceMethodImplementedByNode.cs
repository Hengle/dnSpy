// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnSpy.Analyzer.Properties;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;

namespace dnSpy.Analyzer.TreeNodes {
	sealed class InterfaceMethodImplementedByNode : SearchNode {
		readonly MethodDef analyzedMethod;

		public InterfaceMethodImplementedByNode(MethodDef analyzedMethod) => this.analyzedMethod = analyzedMethod ?? throw new ArgumentNullException(nameof(analyzedMethod));

		protected override void Write(ITextColorWriter output, IDecompiler decompiler) =>
			output.Write(BoxedTextColor.Text, dnSpy_Analyzer_Resources.ImplementedByTreeNode);

		protected override IEnumerable<AnalyzerTreeNodeData> FetchChildren(CancellationToken ct) {
			var analyzer = new ScopedWhereUsedAnalyzer<AnalyzerTreeNodeData>(Context.DocumentService, analyzedMethod, FindReferencesInType);
			return analyzer.PerformAnalysis(ct);
		}

		IEnumerable<AnalyzerTreeNodeData> FindReferencesInType(TypeDef type) {
			if (type.IsInterface)
				yield break;
			var implementedInterfaceRef = GetInterface(type, analyzedMethod.DeclaringType);
			if (implementedInterfaceRef is null)
				yield break;

			foreach (MethodDef method in type.Methods) {
				// Don't include abstract methods, they don't implement anything
				if (!method.IsVirtual || method.IsAbstract)
					continue;
				if (method.HasOverrides && method.Overrides.Any(m => CheckEquals(m.MethodDeclaration.ResolveMethodDef(), analyzedMethod))) {
					yield return new MethodNode(method) { Context = Context };
					yield break;
				}
			}

			foreach (MethodDef method in type.Methods.Where(m => m.Name == analyzedMethod.Name)) {
				// Don't include abstract methods, they don't implement anything
				if (!method.IsVirtual || method.IsAbstract)
					continue;
				if (TypesHierarchyHelpers.MatchInterfaceMethod(method, analyzedMethod, implementedInterfaceRef)) {
					yield return new MethodNode(method) { Context = Context };
					yield break;
				}
			}
		}

		internal static ITypeDefOrRef? GetInterface(TypeDef type, TypeDef interfaceType) {
			foreach (var t in TypesHierarchyHelpers.GetTypeAndBaseTypes(type)) {
				var td = t.Resolve();
				if (td is null)
					break;
				foreach (var ii in td.Interfaces) {
					var genericArgs = t is GenericInstSig ? ((GenericInstSig)t).GenericArguments : null;
					var iface = GenericArgumentResolver.Resolve(ii.Interface.ToTypeSig(), genericArgs, null);
					if (iface is null)
						continue;
					if (new SigComparer().Equals(ii.Interface.GetScopeType(), interfaceType))
						return iface.ToTypeDefOrRef();
				}
			}
			return null;
		}

		public static bool CanShow(MethodDef method) => method.DeclaringType.IsInterface && (method.IsVirtual || method.IsAbstract);
	}
}
