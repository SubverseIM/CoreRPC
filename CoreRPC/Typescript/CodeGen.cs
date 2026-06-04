using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace CoreRPC.Typescript
{
    public static class TypescriptClientGenerator
    {       
        public static string GenerateGlueCode(TypescriptGenerationOptions opts, IEnumerable<Type> types)
        {
            var ctx = new TypescriptTypeMapping(opts);
            var code = new TypescriptCodeBuilder();
            
            var ctor = new List<string>();
            var fields = new List<string>();
            var interfaceFields = new List<string>();

            // Human-readable description of an RPC call, handed to the (optional) custom fetch
            // callback so that calls can be told apart for logging/diagnostics.
            code.BeginInterface("CoreRpcCallInfo");
            code.AppendLines(
                "rpc : string;",
                "method : string;",
                "args : {[name: string] : any};"
            );
            code.End();
            // The init passed to fetch is a plain RequestInit with an extra `coreRpc` field, so it
            // stays assignable to window.fetch's parameter while carrying the call metadata.
            code.AppendLine("export type CoreRpcRequestInit = RequestInit & { coreRpc: CoreRpcCallInfo };");
            code.BeginInterface("CoreApiOptions");
            code.AppendLine("appendCallNamesToQuery? : boolean;");
            code.End();

            foreach (var type in types)
            {
                var proxyClassName = "CoreRpcProxyFor" + type.Name;
                var proxyFieldName = opts.ApiFieldNamingPolicy(type);
                var proxyInterfaceName = "I" + proxyClassName;
                foreach (var iface in new[] {true, false})
                {
                    if (iface)
                        code.BeginInterface(proxyInterfaceName);
                    else
                    {
                        code.BeginClass(proxyClassName + " implements " + proxyInterfaceName);
                        code.AppendLines(
                            $"private parent: {opts.ClassName};",
                            $"constructor (parent: {opts.ClassName}){{this.parent = parent;}}"
                        );
                    }

                    var target = opts.TargetNameExtractor.GetTargetName(type);
                    foreach (var m in type.GetMethods())
                    {
                        if (m.DeclaringType == typeof(System.Object))
                            continue;
                        var sig = Convert.ToBase64String(opts.Binder.GetMethodSignature(m));

                        code.BeginMethod(opts.ApiMethodNamingPolicy(m.Name), iface ? (bool?) null : true);
                        var names = new List<string>();
                        foreach (var p in m.GetParameters())
                        {
                            code.AppendMethodParameter(p.Name, ctx.MapType(p.ParameterType));
                            names.Add(p.Name);
                        }

                        var returnType = m.ReturnType;
                        if (returnType == typeof(Task))
                            returnType = typeof(void);
                        else if (returnType.IsConstructedGenericType &&
                                 returnType.GetGenericTypeDefinition() == typeof(Task<>))
                            returnType = returnType.GetGenericArguments()[0];
                        var returnTypeName = ctx.MapType(returnType);
                        code.AppendMethodReturnValue("Promise<" + returnTypeName + ">");
                        if (iface)
                            code.AppendSemicolon();
                        else
                        {
                            code.BeginBody();
                            var req =
                                $"{{Target: '{target}', MethodSignature: '{sig}', Arguments: [{string.Join(",", names)}] }}";
                            var argMap = string.Join(", ", names.Select(n => $"\"{n}\": {n}"));
                            var info =
                                $"{{rpc: '{target}', method: '{m.Name}', args: {{{argMap}}} }}";
                            code.AppendLine($"return this.parent.send<{returnTypeName}>({req}, {info});");
                            code.End();
                        }
                    }

                    code.End();
                }

                interfaceFields.Add($"{proxyFieldName} : " + proxyInterfaceName + ";");
                fields.Add($"{proxyFieldName} : " + proxyClassName + ";");
                ctor.Add($"this.{proxyFieldName} = new {proxyClassName}(this);");

            }

            var resultFieldName = "Result";
            var exceptionFieldName = "Exception";

            code.BeginInterface("I" + opts.ClassName);
            code.AppendLines(interfaceFields.ToArray());
            code.End();

            code.BeginClass(opts.ClassName + " implements I" + opts.ClassName);
            code.AppendLines(
                "private baseUrl: string;",
                "private fetch: (url: string, init: CoreRpcRequestInit) => Promise<Response>;",
                "public appendCallNamesToQuery: boolean;",

                "constructor(baseUrl : string, customFetch?: (url: string, init: CoreRpcRequestInit) => Promise<Response>, options?: CoreApiOptions) {",
                "this.baseUrl = baseUrl;",
                "if(customFetch) this.fetch = customFetch; else this.fetch =  (r, i) => fetch(r, i);",
                "this.appendCallNamesToQuery = !!(options && options.appendCallNamesToQuery);"
            );
            code.AppendLines(ctor.ToArray());
            code.AppendLine("}");


            code.AppendLines(
                "public send<T>(request: any, coreRpc: CoreRpcCallInfo) : Promise<T>{",
                "let url = this.baseUrl;",
                "if (this.appendCallNamesToQuery) {",
                "    const sep = url.indexOf('?') < 0 ? '?' : '&';",
                "    url = url + sep + 'rpc=' + encodeURIComponent(coreRpc.rpc) + '&method=' + encodeURIComponent(coreRpc.method);",
                "}",
                "return this.fetch(url, {method: 'post', body: JSON.stringify(request), coreRpc: coreRpc})",
                "    .then(response => {",
                "        if (!response.ok)",
                "            throw new Error(response.statusText);",
                "         return response.json();",
                "    }).then(jr => {",
                $"        const r = <{{{resultFieldName}?: T, {exceptionFieldName}?: string}}>jr;",
                $"        if(r.{exceptionFieldName}) throw r.{exceptionFieldName};",
                $"        return r.{resultFieldName}!;",
                "    });",
                "}"
            );
            code.AppendLines(fields.ToArray());
            code.End();

            foreach (var attg in opts.AdditionalTypes)
                ctx.MapType(attg);
            return (ctx + "\n" + code).Replace("\r\n", "\n");
        }

    }
}