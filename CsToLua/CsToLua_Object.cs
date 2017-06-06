﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Semantics;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Symbols;

namespace RoslynTool.CsToLua
{
    internal partial class CsLuaTranslater
    {
        #region OO语法框架部分
        public override void VisitCompilationUnit(CompilationUnitSyntax node)
        {
            base.VisitCompilationUnit(node);
        }
        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            /*
            //--别名语法处理在这里跳过
            //--using alias = namespaces;
            //--extern alias Name;
            */

            /*
            为了支持partial类，命名空间不能写成嵌套形式
            ns1 = {
                ns2= {
                },
            }
            而必须写成赋值语句的形式，所以这里不需要缩进
            */

            var members = node.Members;
            foreach (var member in members) {
                member.Accept(this);
            }
        }
        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            //允许在C#里定义与使用接口，lua里隐式支持任何接口
            INamedTypeSymbol sym = m_Model.GetDeclaredSymbol(node);
            if (SymbolTable.Instance.IsCs2LuaSymbol(sym)) {
                var fullName = ClassInfo.GetFullName(sym);

                Dictionary<string, List<string>> intfs;
                if (m_ClassInfoStack.Count <= 0) {
                    intfs = SymbolTable.Instance.Cs2DslInterfaces;
                } else {
                    intfs = m_ClassInfoStack.Peek().InnerInterfaces;
                }

                List<string> list;
                if (!intfs.TryGetValue(fullName, out list)) {
                    list = new List<string>();
                    intfs.Add(fullName, list);
                }
                foreach (var intf in sym.AllInterfaces) {
                    var fn = ClassInfo.GetFullName(intf);
                    if (!list.Contains(fn)) {
                        list.Add(fn);
                    }
                }
            }
        }
        public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            INamedTypeSymbol sym = m_Model.GetDeclaredSymbol(node);
            ClassInfo ci = new ClassInfo();
            ClassSymbolInfo info;
            SymbolTable.Instance.ClassSymbols.TryGetValue(ClassInfo.GetFullName(sym), out info);
            ci.Init(sym, info);
            m_ClassInfoStack.Push(ci);

            if (m_ClassInfoStack.Count == 1) {
                ci.BeforeOuterCodeBuilder.Append(m_ToplevelCodeBuilder.ToString());
                m_ToplevelCodeBuilder.Clear();
            }
            
            ++m_Indent;
            foreach (var member in node.Members) {
                VisitEnumMemberDeclaration(member);
            }
            --m_Indent;

            m_ClassInfoStack.Pop();

            if (m_ClassInfoStack.Count <= 0) {
                AddToplevelClass(ci.Key, ci);
            } else {
                m_ClassInfoStack.Peek().InnerClasses.Add(ci.Key, ci);
            }

            if (m_Indent <= 0 && null != m_LastToplevelClass) {
                m_LastToplevelClass.AfterOuterCodeBuilder.Append(m_ToplevelCodeBuilder.ToString());
                m_ToplevelCodeBuilder.Clear();
            }
        }
        public override void VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node)
        {
            var ci = m_ClassInfoStack.Peek();
            IFieldSymbol sym = m_Model.GetDeclaredSymbol(node);

            ci.CurrentCodeBuilder = ci.StaticFieldCodeBuilder;
            CodeBuilder.AppendFormat("{0}[\"{1}\"]", GetIndentString(), node.Identifier.Text);
            if (sym.HasConstantValue) {
                CodeBuilder.Append(" = ");
                OutputConstValue(sym.ConstantValue, sym);
                CodeBuilder.AppendLine(",");

                ++m_Indent;
                
                ci.CurrentCodeBuilder = ci.EnumValue2StringCodeBuilder;
                CodeBuilder.AppendFormat("{0}[", GetIndentString());
                OutputConstValue(sym.ConstantValue, sym);
                CodeBuilder.AppendFormat("] = \"{0}\",", sym.Name);
                CodeBuilder.AppendLine();

                --m_Indent;
            } else if (null != node.EqualsValue) {
                CodeBuilder.AppendFormat(" {0} ", node.EqualsValue.EqualsToken.Text);
                OutputExpressionSyntax(node.EqualsValue.Value);
                CodeBuilder.AppendLine(",");
                
                ++m_Indent;

                ci.CurrentCodeBuilder = ci.EnumValue2StringCodeBuilder;
                CodeBuilder.AppendFormat("{0}[", GetIndentString());
                OutputExpressionSyntax(node.EqualsValue.Value);
                CodeBuilder.AppendFormat("] = \"{0}\",", sym.Name);
                CodeBuilder.AppendLine();

                --m_Indent;
            } else {
                Log(node, "enum member can't deduce a value !");
                CodeBuilder.Append(" = 0");
                CodeBuilder.AppendLine(",");
            }
        }
        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            VisitTypeDeclarationSyntax(node);
        }
        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            VisitTypeDeclarationSyntax(node);
        }
        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            bool isExportConstructor = false;
            var ci = m_ClassInfoStack.Peek();
            IMethodSymbol declSym = m_Model.GetDeclaredSymbol(node);
            if (null != declSym) {
                string require = ClassInfo.GetAttributeArgument<string>(declSym, "Cs2Lua.RequireAttribute", 0);
                if (!string.IsNullOrEmpty(require)) {
                    SymbolTable.Instance.AddRequire(ci.Key, require);
                }
                if (ClassInfo.HasAttribute(declSym, "Cs2Lua.IgnoreAttribute"))
                    return;
                if (declSym.IsAbstract)
                    return;
                isExportConstructor = ClassInfo.HasAttribute(declSym, "Cs2Lua.ExportAttribute");
            }

            bool generateBasicCtor = false;
            bool generateBasicCctor = false;
            ClassSymbolInfo csi;
            if (SymbolTable.Instance.ClassSymbols.TryGetValue(ci.Key, out csi)) {
                generateBasicCtor = csi.GenerateBasicCtor;
                generateBasicCctor = csi.GenerateBasicCctor;
            }

            bool isStatic = declSym.IsStatic;
            var mi = new MethodInfo();
            mi.Init(declSym, node);
            m_MethodInfoStack.Push(mi);

            string manglingName = NameMangling(declSym);
            if (isStatic) {
                ci.ExistStaticConstructor = true;
            } else {
                ci.ExistConstructor = true;

                if (isExportConstructor) {
                    ci.ExportConstructor = manglingName;
                    ci.ExportConstructorInfo = mi;
                } else if (string.IsNullOrEmpty(ci.ExportConstructor)) {
                    //有构造但还没有明确指定的导出构造，则使用第一次遇到的构造
                    ci.ExportConstructor = manglingName;
                    ci.ExportConstructorInfo = mi;
                }
            }

            bool myselfDefinedBaseClass = SymbolTable.Instance.IsCs2LuaSymbol(ci.SemanticInfo.BaseType);
            CodeBuilder.AppendFormat("{0}{1} = function({2}", GetIndentString(), manglingName, isStatic ? string.Empty : "this");
            if (mi.ParamNames.Count > 0) {
                if (!isStatic) {
                    CodeBuilder.Append(", ");
                }
                CodeBuilder.Append(string.Join(", ", mi.ParamNames.ToArray()));
            }
            CodeBuilder.Append(")");
            CodeBuilder.AppendLine();
            ++m_Indent;
            if (mi.ValueParams.Count > 0) {
                OutputWrapValueParams(CodeBuilder, mi);
            }
            if (!string.IsNullOrEmpty(mi.OriginalParamsName)) {
                if (mi.ParamsIsValueType) {
                    CodeBuilder.AppendFormat("{0}local {1} = wrapvaluetypearray{{...}};", GetIndentString(), mi.OriginalParamsName);
                } else if (mi.ParamsIsExternValueType) {
                    CodeBuilder.AppendFormat("{0}local {1} = wrapexternvaluetypearray{{...}};", GetIndentString(), mi.OriginalParamsName);
                } else {
                    CodeBuilder.AppendFormat("{0}local {1} = wraparray{{...}};", GetIndentString(), mi.OriginalParamsName);
                }
                CodeBuilder.AppendLine();
            }
            if (SymbolTable.ForXlua && mi.OutParamNames.Count > 0) {
                CodeBuilder.AppendFormat("{0}local {1};", GetIndentString(), string.Join(", ", mi.OutParamNames.ToArray()));
                CodeBuilder.AppendLine();
            }
            //首先执行初始化列表
            var init = node.Initializer;
            if (null != init) {
                var oper = m_Model.GetOperation(init) as IInvocationExpression;
                string manglingName2 = NameMangling(oper.TargetMethod);
                if (init.ThisOrBaseKeyword.Text == "this") {
                    CodeBuilder.AppendFormat("{0}this:{1}(", GetIndentString(), manglingName2);
                } else if (init.ThisOrBaseKeyword.Text == "base") {
                    CodeBuilder.AppendFormat("{0}this.base.{1}(this", GetIndentString(), manglingName2);
                    if (init.ArgumentList.Arguments.Count > 0) {
                        CodeBuilder.Append(", ");
                    }
                }
                VisitArgumentList(init.ArgumentList);
                CodeBuilder.AppendLine(");");
            }
            //再执行构造函数内容（字段初始化部分）
            if (isStatic) {
                if (!string.IsNullOrEmpty(ci.BaseKey) && myselfDefinedBaseClass) {
                    CodeBuilder.AppendFormat("{0}{1}.cctor();", GetIndentString(), ci.BaseKey);
                    CodeBuilder.AppendLine();
                }
                if (generateBasicCctor) {
                    CodeBuilder.AppendFormat("{0}{1}.__cctor();", GetIndentString(), ci.Key);
                    CodeBuilder.AppendLine();
                }
            } else {
                if (!string.IsNullOrEmpty(ci.BaseKey) && !ClassInfo.IsBaseInitializerCalled(node, m_Model) && myselfDefinedBaseClass) {
                    //如果当前构造没有调父类构造并且委托的其它构造也没有调父类构造，则调用默认构造。
                    CodeBuilder.AppendFormat("{0}this.base.ctor(this);", GetIndentString());
                    CodeBuilder.AppendLine();
                }
                if (generateBasicCtor) {
                    CodeBuilder.AppendFormat("{0}this:__ctor({1});", GetIndentString(), string.Join(", ", mi.GenericTypeTypeParamNames.ToArray()));
                    CodeBuilder.AppendLine();
                }
            }
            //再执行构造函数内容（构造函数部分）
            if (null != node.Body) {
                VisitBlock(node.Body);
            }
            if (!mi.ExistTopLevelReturn) {
                if (mi.ReturnParamNames.Count > 0) {
                    CodeBuilder.AppendFormat("{0}return this, {1};", GetIndentString(), string.Join(", ", mi.ReturnParamNames));
                    CodeBuilder.AppendLine();
                } else if (!isStatic) {
                    CodeBuilder.AppendFormat("{0}return this;", GetIndentString());
                    CodeBuilder.AppendLine();
                }
            }
            --m_Indent;
            CodeBuilder.AppendFormat("{0}end,", GetIndentString());
            CodeBuilder.AppendLine();
            m_MethodInfoStack.Pop();
        }
        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            IMethodSymbol declSym = m_Model.GetDeclaredSymbol(node);
            VisitCommonMethodDeclaration(declSym, node, node.Body, node.ExpressionBody);
        }
        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            var ci = m_ClassInfoStack.Peek();
            IPropertySymbol declSym = m_Model.GetDeclaredSymbol(node);
            if (null != declSym) {
                string require = ClassInfo.GetAttributeArgument<string>(declSym, "Cs2Lua.RequireAttribute", 0);
                if (!string.IsNullOrEmpty(require)) {
                    SymbolTable.Instance.AddRequire(ci.Key, require);
                }
                if (ClassInfo.HasAttribute(declSym, "Cs2Lua.IgnoreAttribute"))
                    return;
                if (declSym.IsAbstract)
                    return;
            }

            bool noimpl = true;
            foreach (var accessor in node.AccessorList.Accessors) {
                var sym = m_Model.GetDeclaredSymbol(accessor);
                if (null != accessor.Body) {
                    noimpl = false;
                    break;
                }
            }

            if (noimpl) {
                //退化为field
                StringBuilder curBuilder = ci.CurrentCodeBuilder;

                if (declSym.IsStatic) {
                    ci.CurrentCodeBuilder = ci.StaticFieldCodeBuilder;
                } else {
                    ci.CurrentCodeBuilder = ci.InstanceFieldCodeBuilder;
                }
                
                ++m_Indent;
                CodeBuilder.AppendFormat("{0}{1} = ", GetIndentString(), SymbolTable.GetPropertyName(declSym));
                if (null != node.Initializer) {
                    IConversionExpression opd = null;
                    var oper = m_Model.GetOperation(node.Initializer) as ISymbolInitializer;
                    if (null != oper) {
                        opd = oper.Value as IConversionExpression;
                    }
                    CodeBuilder.AppendFormat("{0}", declSym.Type.TypeKind == TypeKind.Delegate ? "delegationwrap(" : string.Empty);
                    OutputExpressionSyntax(node.Initializer.Value, opd);
                    CodeBuilder.AppendFormat("{0}", declSym.Type.TypeKind == TypeKind.Delegate ? ")" : string.Empty);
                    CodeBuilder.Append(",");
                } else if (declSym.Type.TypeKind == TypeKind.Delegate) {
                    CodeBuilder.Append("wrapdelegation{},");
                } else {
                    OutputDefaultValue(declSym.Type);
                    CodeBuilder.Append(",");
                }
                CodeBuilder.AppendLine();
                --m_Indent;

                ci.CurrentCodeBuilder = curBuilder;
            } else {
                StringBuilder curBuilder = ci.CurrentCodeBuilder;
                if(declSym.IsStatic) {
                    ci.CurrentCodeBuilder = ci.StaticFunctionCodeBuilder;
                } else {
                    ci.CurrentCodeBuilder = ci.InstanceFunctionCodeBuilder;
                }
                foreach (var accessor in node.AccessorList.Accessors) {
                    var sym = m_Model.GetDeclaredSymbol(accessor);
                    if (null != sym) {
                        var mi = new MethodInfo();
                        mi.Init(sym, accessor);
                        m_MethodInfoStack.Push(mi);

                        string manglingName = NameMangling(sym);
                        string keyword = accessor.Keyword.Text;
                        string paramStr = string.Join(", ", mi.ParamNames.ToArray());
                        if (!declSym.IsStatic) {
                            if (string.IsNullOrEmpty(paramStr))
                                paramStr = "this";
                            else
                                paramStr = "this, " + paramStr;
                        }
                        CodeBuilder.AppendFormat("{0}{1} = {2}function({3})", GetIndentString(), manglingName, mi.ExistYield ? "wrapenumerable(" : string.Empty, paramStr);
                        CodeBuilder.AppendLine();
                        ++m_Indent;
                        bool isStatic = declSym.IsStatic;
                        string luaModule = ClassInfo.GetAttributeArgument<string>(sym, "Cs2Lua.TranslateToAttribute", 0);
                        string luaFuncName = ClassInfo.GetAttributeArgument<string>(sym, "Cs2Lua.TranslateToAttribute", 1);
                        if (!string.IsNullOrEmpty(luaModule) || !string.IsNullOrEmpty(luaFuncName)) {
                            if (!string.IsNullOrEmpty(luaModule)) {
                                SymbolTable.Instance.AddRequire(ci.Key, luaModule);
                            }
                            if (sym.ReturnsVoid && mi.ReturnParamNames.Count <= 0) {
                                CodeBuilder.AppendFormat("{0}{1}({2}", GetIndentString(), luaFuncName, isStatic ? string.Empty : "this");
                            } else {
                                CodeBuilder.AppendFormat("{0}return {1}({2}", GetIndentString(), luaFuncName, isStatic ? string.Empty : "this");
                            }
                            if (mi.ParamNames.Count > 0) {
                                if (!isStatic) {
                                    CodeBuilder.Append(", ");
                                }
                                CodeBuilder.Append(string.Join(", ", mi.ParamNames.ToArray()));
                            }
                            CodeBuilder.AppendLine(");");
                        } else if (null != accessor.Body) {
                            if (mi.ValueParams.Count > 0) {
                                OutputWrapValueParams(CodeBuilder, mi);
                            }
                            VisitBlock(accessor.Body);
                        }
                        --m_Indent;
                        CodeBuilder.AppendFormat("{0}end{1},", GetIndentString(), mi.ExistYield ? ")" : string.Empty);
                        CodeBuilder.AppendLine();

                        m_MethodInfoStack.Pop();
                    }
                }
                ci.CurrentCodeBuilder = curBuilder;

                CodeBuilder.AppendFormat("{0}{1} = {{", GetIndentString(), SymbolTable.GetPropertyName(declSym));
                CodeBuilder.AppendLine();
                ++m_Indent;
                foreach (var accessor in node.AccessorList.Accessors) {
                    var sym = m_Model.GetDeclaredSymbol(accessor);
                    if (null != sym) {
                        string manglingName = NameMangling(sym);
                        string keyword = accessor.Keyword.Text;
                        CodeBuilder.AppendFormat("{0}{1} = {2}.{3},", GetIndentString(), keyword, declSym.IsStatic ? "static_methods" : "instance_methods", manglingName);
                        CodeBuilder.AppendLine();
                    }
                }
                --m_Indent;
                CodeBuilder.AppendFormat("{0}", GetIndentString());
                CodeBuilder.AppendLine("},");
            }
        }
        public override void VisitEventDeclaration(EventDeclarationSyntax node)
        {
            var ci = m_ClassInfoStack.Peek();
            IEventSymbol declSym = m_Model.GetDeclaredSymbol(node);
            if (null != declSym) {
                string require = ClassInfo.GetAttributeArgument<string>(declSym, "Cs2Lua.RequireAttribute", 0);
                if (!string.IsNullOrEmpty(require)) {
                    SymbolTable.Instance.AddRequire(ci.Key, require);
                }
                if (ClassInfo.HasAttribute(declSym, "Cs2Lua.IgnoreAttribute"))
                    return;
                if (declSym.IsAbstract)
                    return;
            }
            
            StringBuilder curBuilder = ci.CurrentCodeBuilder;
            if (declSym.IsStatic) {
                ci.CurrentCodeBuilder = ci.StaticFunctionCodeBuilder;
            } else {
                ci.CurrentCodeBuilder = ci.InstanceFunctionCodeBuilder;
            }
            foreach (var accessor in node.AccessorList.Accessors) {
                var sym = m_Model.GetDeclaredSymbol(accessor);
                if (null != sym) {
                    var mi = new MethodInfo();
                    mi.Init(sym, accessor);
                    m_MethodInfoStack.Push(mi);

                    string manglingName = NameMangling(sym);
                    string keyword = accessor.Keyword.Text;
                    string paramStr = string.Join(", ", mi.ParamNames.ToArray());
                    if(!declSym.IsStatic){
                        if (string.IsNullOrEmpty(paramStr))
                            paramStr = "this";
                        else
                            paramStr = "this, "+paramStr;
                    }
                    CodeBuilder.AppendFormat("{0}{1} = function({2})", GetIndentString(), manglingName, paramStr);
                    CodeBuilder.AppendLine();
                    ++m_Indent;
                    bool isStatic = declSym.IsStatic;
                    string luaModule = ClassInfo.GetAttributeArgument<string>(sym, "Cs2Lua.TranslateToAttribute", 0);
                    string luaFuncName = ClassInfo.GetAttributeArgument<string>(sym, "Cs2Lua.TranslateToAttribute", 1);
                    if (!string.IsNullOrEmpty(luaModule) || !string.IsNullOrEmpty(luaFuncName)) {
                        if (!string.IsNullOrEmpty(luaModule)) {
                            SymbolTable.Instance.AddRequire(ci.Key, luaModule);
                        }
                        if (sym.ReturnsVoid && mi.ReturnParamNames.Count <= 0) {
                            CodeBuilder.AppendFormat("{0}{1}({2}", GetIndentString(), luaFuncName, isStatic ? string.Empty : "this");
                        } else {
                            CodeBuilder.AppendFormat("{0}return {1}({2}", GetIndentString(), luaFuncName, isStatic ? string.Empty : "this");
                        }
                        if (mi.ParamNames.Count > 0) {
                            if (!isStatic) {
                                CodeBuilder.Append(", ");
                            }
                            CodeBuilder.Append(string.Join(", ", mi.ParamNames.ToArray()));
                        }
                        CodeBuilder.AppendLine(");");
                    } else if (null != accessor.Body) {
                        if (mi.ValueParams.Count > 0) {
                            OutputWrapValueParams(CodeBuilder, mi);
                        }
                        VisitBlock(accessor.Body);
                    }
                    --m_Indent;
                    CodeBuilder.AppendFormat("{0}end,", GetIndentString());
                    CodeBuilder.AppendLine();

                    m_MethodInfoStack.Pop();
                }
            }
            ci.CurrentCodeBuilder = curBuilder;

            CodeBuilder.AppendFormat("{0}{1} = {{", GetIndentString(), SymbolTable.GetEventName(declSym));
            CodeBuilder.AppendLine();
            ++m_Indent;
            foreach (var accessor in node.AccessorList.Accessors) {
                var sym = m_Model.GetDeclaredSymbol(accessor);
                if (null != sym) {
                    string manglingName = NameMangling(sym);
                    string keyword = accessor.Keyword.Text;
                    CodeBuilder.AppendFormat("{0}{1} = {2}.{3},", GetIndentString(), keyword, declSym.IsStatic ? "static_methods" : "instance_methods", manglingName);
                    CodeBuilder.AppendLine();
                }
            }
            --m_Indent;
            CodeBuilder.AppendFormat("{0}", GetIndentString());
            CodeBuilder.AppendLine("},");
        }
        public override void VisitOperatorDeclaration(OperatorDeclarationSyntax node)
        {
            IMethodSymbol declSym = m_Model.GetDeclaredSymbol(node);
            VisitCommonMethodDeclaration(declSym, node, node.Body, node.ExpressionBody);
        }
        public override void VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
        {
            IMethodSymbol declSym = m_Model.GetDeclaredSymbol(node);
            VisitCommonMethodDeclaration(declSym, node, node.Body, node.ExpressionBody);
        }
        public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node)
        {
            var ci = m_ClassInfoStack.Peek();
            var declSym = m_Model.GetDeclaredSymbol(node);
            if (null != declSym) {
                string require = ClassInfo.GetAttributeArgument<string>(declSym, "Cs2Lua.RequireAttribute", 0);
                if (!string.IsNullOrEmpty(require)) {
                    SymbolTable.Instance.AddRequire(ci.Key, require);
                }
                if (ClassInfo.HasAttribute(declSym, "Cs2Lua.IgnoreAttribute"))
                    return;
                if (declSym.IsAbstract)
                    return;
            }

            StringBuilder currentBuilder = ci.CurrentCodeBuilder;
            if (declSym.IsStatic)
                ci.CurrentCodeBuilder = ci.StaticFunctionCodeBuilder;
            else
                ci.CurrentCodeBuilder = ci.InstanceFunctionCodeBuilder;
            
            foreach (var accessor in node.AccessorList.Accessors) {
                var sym = m_Model.GetDeclaredSymbol(accessor);
                if (null != sym) {
                    var mi = new MethodInfo();
                    mi.Init(sym, accessor);
                    m_MethodInfoStack.Push(mi);

                    string manglingName = NameMangling(sym);
                    string keyword = accessor.Keyword.Text;
                    CodeBuilder.AppendFormat("{0}{1} = function(this, {2})", GetIndentString(), manglingName, string.Join(", ", mi.ParamNames.ToArray()));
                    CodeBuilder.AppendLine();
                    ++m_Indent;
                    bool isStatic = declSym.IsStatic;
                    string luaModule = ClassInfo.GetAttributeArgument<string>(sym, "Cs2Lua.TranslateToAttribute", 0);
                    string luaFuncName = ClassInfo.GetAttributeArgument<string>(sym, "Cs2Lua.TranslateToAttribute", 1);
                    if (!string.IsNullOrEmpty(luaModule) || !string.IsNullOrEmpty(luaFuncName)) {
                        if (!string.IsNullOrEmpty(luaModule)) {
                            SymbolTable.Instance.AddRequire(ci.Key, luaModule);
                        }
                        if (sym.ReturnsVoid && mi.ReturnParamNames.Count <= 0) {
                            CodeBuilder.AppendFormat("{0}{1}({2}", GetIndentString(), luaFuncName, isStatic ? string.Empty : "this");
                        } else {
                            CodeBuilder.AppendFormat("{0}return {1}({2}", GetIndentString(), luaFuncName, isStatic ? string.Empty : "this");
                        }
                        if (mi.ParamNames.Count > 0) {
                            if (!isStatic) {
                                CodeBuilder.Append(", ");
                            }
                            CodeBuilder.Append(string.Join(", ", mi.ParamNames.ToArray()));
                        }
                        CodeBuilder.AppendLine(");");
                    } else if (null != accessor.Body) {
                        if (mi.ValueParams.Count > 0) {
                            OutputWrapValueParams(CodeBuilder, mi);
                        }
                        if (!string.IsNullOrEmpty(mi.OriginalParamsName)) {
                            if (keyword == "get") {
                                if (mi.ParamsIsValueType) {
                                    CodeBuilder.AppendFormat("{0}local {1} = wrapvaluetypearray{{...}};", GetIndentString(), mi.OriginalParamsName);
                                } else if (mi.ParamsIsExternValueType) {
                                    CodeBuilder.AppendFormat("{0}local {1} = wrapexternvaluetypearray{{...}};", GetIndentString(), mi.OriginalParamsName);
                                } else {
                                    CodeBuilder.AppendFormat("{0}local {1} = wraparray{{...}};", GetIndentString(), mi.OriginalParamsName);
                                }
                                CodeBuilder.AppendLine();
                            } else {
                                CodeBuilder.AppendFormat("{0}local {1} = {{...}};", GetIndentString(), mi.OriginalParamsName);
                                CodeBuilder.AppendLine();
                                CodeBuilder.AppendFormat("{0}local value = table.remove({1});", GetIndentString(), mi.OriginalParamsName);
                                CodeBuilder.AppendLine();
                                if (mi.ParamsIsValueType) {
                                    CodeBuilder.AppendFormat("{0}{1} = wrapvaluetypearray({2});", GetIndentString(), mi.OriginalParamsName, mi.OriginalParamsName);
                                } else if (mi.ParamsIsExternValueType) {
                                    CodeBuilder.AppendFormat("{0}{1} = wrapexternvaluetypearray({2});", GetIndentString(), mi.OriginalParamsName, mi.OriginalParamsName);
                                } else {
                                    CodeBuilder.AppendFormat("{0}{1} = wraparray({2});", GetIndentString(), mi.OriginalParamsName, mi.OriginalParamsName);
                                }
                                CodeBuilder.AppendLine();
                            }
                        }
                        VisitBlock(accessor.Body);
                    }
                    --m_Indent;
                    CodeBuilder.AppendFormat("{0}end,", GetIndentString());
                    CodeBuilder.AppendLine();

                    m_MethodInfoStack.Pop();
                }
            }

            ci.CurrentCodeBuilder = currentBuilder;
        }
        public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
        {
            SymbolInfo symInfo = m_Model.GetSymbolInfo(node);
            var sym = symInfo.Symbol as IMethodSymbol;

            if (null != sym && sym.Parameters.Length == 1) {
                if (node.Body is BlockSyntax) {
                    var param = sym.Parameters[0];
                    CodeBuilder.AppendFormat("(function({0})", param.Name);
                    CodeBuilder.AppendLine();
                    ++m_Indent;
                    node.Body.Accept(this);
                    if (!sym.ReturnsVoid) {
                        CodeBuilder.AppendFormat("{0}return {1};", GetIndentString(), param.Name);
                        CodeBuilder.AppendLine();
                    }
                    --m_Indent;
                    CodeBuilder.AppendFormat("{0}end)", GetIndentString());
                } else {
                    var param = sym.Parameters[0];
                    CodeBuilder.AppendFormat("(function({0}) return ", param.Name);
                    IConversionExpression opd = null;
                    var oper = m_Model.GetOperation(node) as ILambdaExpression;
                    if (null != oper && oper.Body.Statements.Length == 1) {
                        var iret = oper.Body.Statements[0] as IReturnStatement;
                        if (null != iret) {
                            opd = iret.ReturnedValue as IConversionExpression;
                        }
                    }
                    var exp = node.Body as ExpressionSyntax;
                    if (null != exp) {
                        OutputExpressionSyntax(exp, opd);
                    } else {
                        ReportIllegalSymbol(node, symInfo);
                    }
                    CodeBuilder.Append("; end)");
                }
            } else {
                ReportIllegalSymbol(node, symInfo);
            }
        }
        public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
        {
            SymbolInfo symInfo = m_Model.GetSymbolInfo(node);
            var sym = symInfo.Symbol as IMethodSymbol;
            if (null != sym) {
                var mi = new MethodInfo();
                mi.Init(sym, node);
                m_MethodInfoStack.Push(mi);

                CodeBuilder.Append("(function(");
                CodeBuilder.Append(string.Join(", ", mi.ParamNames.ToArray()));
                if (node.Body is BlockSyntax) {
                    CodeBuilder.AppendLine(")");
                    ++m_Indent;
                    if (mi.ValueParams.Count > 0) {
                        OutputWrapValueParams(CodeBuilder, mi);
                    }
                    if (!string.IsNullOrEmpty(mi.OriginalParamsName)) {
                        if (mi.ParamsIsValueType) {
                            CodeBuilder.AppendFormat("{0}local {1} = wrapvaluetypearray{{...}};", GetIndentString(), mi.OriginalParamsName);
                        } else if (mi.ParamsIsExternValueType) {
                            CodeBuilder.AppendFormat("{0}local {1} = wrapexternvaluetypearray{{...}};", GetIndentString(), mi.OriginalParamsName);
                        } else {
                            CodeBuilder.AppendFormat("{0}local {1} = wraparray{{...}};", GetIndentString(), mi.OriginalParamsName);
                        }
                        CodeBuilder.AppendLine();
                    }
                    if (SymbolTable.ForXlua && mi.OutParamNames.Count > 0) {
                        CodeBuilder.AppendFormat("{0}local {1};", GetIndentString(), string.Join(", ", mi.OutParamNames.ToArray()));
                        CodeBuilder.AppendLine();
                    }
                    node.Body.Accept(this);
                    if (!mi.ExistTopLevelReturn && mi.ReturnParamNames.Count > 0) {
                        CodeBuilder.AppendFormat("{0}return {1};", GetIndentString(), string.Join(", ", mi.ReturnParamNames));
                        CodeBuilder.AppendLine();
                    }
                    --m_Indent;
                    CodeBuilder.AppendFormat("{0}end)", GetIndentString());
                } else {
                    string varName = string.Format("__compiler_lambda_{0}", node.GetLocation().GetLineSpan().StartLinePosition.Line);
                    CodeBuilder.AppendFormat(") ");
                    if (mi.ReturnParamNames.Count > 0) {
                        CodeBuilder.AppendFormat("local {0} = ", varName);
                    } else {
                        CodeBuilder.Append("return ");
                    }
                    IConversionExpression opd = null;
                    var oper = m_Model.GetOperation(node) as ILambdaExpression;
                    if (null != oper && oper.Body.Statements.Length == 1) {
                        var iret = oper.Body.Statements[0] as IReturnStatement;
                        if (null != iret) {
                            opd = iret.ReturnedValue as IConversionExpression;
                        }
                    }
                    var exp = node.Body as ExpressionSyntax;
                    if (null != exp) {
                        OutputExpressionSyntax(exp, opd);
                    } else {
                        ReportIllegalSymbol(node, symInfo);
                    }
                    if (mi.ReturnParamNames.Count > 0) {
                        CodeBuilder.AppendFormat("; return {0}, {1}", varName, string.Join(", ", mi.ReturnParamNames));
                    }
                    CodeBuilder.Append("; end)");
                }
                m_MethodInfoStack.Pop();
            } else {
                ReportIllegalSymbol(node, symInfo);
            }
        }
        public override void VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
        {
            SymbolInfo symInfo = m_Model.GetSymbolInfo(node);
            var sym = symInfo.Symbol as IMethodSymbol;
            if (null != sym) {
                var mi = new MethodInfo();
                mi.Init(sym, node);
                m_MethodInfoStack.Push(mi);

                CodeBuilder.Append("(function(");
                int ct = sym.Parameters.Length;
                if (ct > 0) {
                    for (int i = 0; i < ct; ++i) {
                        var param = sym.Parameters[i];
                        CodeBuilder.Append(param.Name);
                        if (i < ct - 1) {
                            CodeBuilder.Append(", ");
                        }
                    }
                }
                CodeBuilder.AppendLine(")");
                ++m_Indent;
                node.Body.Accept(this);
                --m_Indent;
                CodeBuilder.AppendFormat("{0}end)", GetIndentString());

                m_MethodInfoStack.Pop();
            } else {
                ReportIllegalSymbol(node, symInfo);
            }
        }
        public override void VisitBlock(BlockSyntax node)
        {
            bool isBlock = node.Parent is BlockSyntax;
            if (isBlock) {
                CodeBuilder.AppendFormat("{0}do", GetIndentString());
                CodeBuilder.AppendLine();
                ++m_Indent;
            }
            base.VisitBlock(node);
            if (isBlock) {
                --m_Indent;
                CodeBuilder.AppendFormat("{0}end;", GetIndentString());
                CodeBuilder.AppendLine();
            }
        }
        public override void VisitUsingStatement(UsingStatementSyntax node)
        {
            string varName = string.Format("__compiler_using_{0}", node.GetLocation().GetLineSpan().StartLinePosition.Line);
            if (null != node.Declaration) {
                VisitVariableDeclaration(node.Declaration);
            } else if (null != node.Expression) {
                CodeBuilder.AppendFormat("{0}local {1} = ", GetIndentString(), varName);
                IConversionExpression opd = m_Model.GetOperation(node.Expression) as IConversionExpression;
                OutputExpressionSyntax(node.Expression, opd);
                CodeBuilder.AppendLine(";");
            } else {
                Log(node, "node.Declaration and node.Expression are null.");
            }
            if (null != node.Statement) {
                node.Statement.Accept(this);
            } else {
                Log(node, "node.Statement is null.");
            }
            if (null != node.Declaration && null != node.Declaration.Variables) {
                foreach (var decl in node.Declaration.Variables) {
                    CodeBuilder.AppendFormat("{0}{1}:Dispose();", GetIndentString(), decl.Identifier.Text);
                    CodeBuilder.AppendLine();
                }
            } else if (null != node.Expression) {
                CodeBuilder.AppendFormat("{0}{1}:Dispose();", GetIndentString(), varName);
                CodeBuilder.AppendLine();
            } else {
                Log(node, "node.Declaration is null or node.Declaration.Variables is null, and node.Expression is null.");
            }
        }
        #endregion
    }
}