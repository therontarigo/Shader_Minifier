﻿module Printer

open System
open System.Linq
open System.Collections.Generic
open Ast
open Options.Globals
open System.Text.RegularExpressions

// Whatever .NET / C# / F# think of strings and characters is irrelevant; the shader is plain bytes ASCII or UTF-8 and kkp formats use real byte offsets, not "characters".
// --> need to stop using (minifiedShader: string) and use (minifiedShader: byte array) instead.
//   (instead of the "cannot process a non-byte char" hack.)
// Of course it is only theoretical and in practice minified shaders are pure ASCII.

let private kkpSymFormat shaderSymbol minifiedSize (symbolPool: string array) (symbolIndexes: int16 array) =
    // https://github.com/ConspiracyHu/kkpView-public/blob/main/README.md
    let bytes = List<byte>(capacity = 2 * symbolIndexes.Length)
    let ascii (s: string) = bytes.AddRange(System.Text.Encoding.ASCII.GetBytes s)
    let asciiz s = ascii s; bytes.Add(byte 0)
    let fourByteInteger n = bytes.AddRange(List.map byte [ n; n >>> 8; n >>> 16; n >>> 24 ])
    let twoByteInteger n = bytes.AddRange(List.map byte [ n; n >>> 8 ])
    ascii "PHXP"                        // 4 bytes: FOURCC: 'PHXP'
    asciiz shaderSymbol                 // ASCIIZ string: name of the shader described by this sym file.
    fourByteInteger minifiedSize        // 4 bytes: minified data size (Ds) of the shader.
    fourByteInteger symbolPool.Length   // 4 bytes: symbol count (Sc).
    for symbolName in symbolPool do     // For each symbol (Sc),
        asciiz symbolName               //     ASCIIZ string: name of the symbol.
    for symbolIndex in symbolIndexes do // For each byte in the minified shader (Ds),
        twoByteInteger symbolIndex      //     2 bytes: symbol index in the symbol pool.
    bytes.ToArray()

type SymbolMap() =
    let symbolRefs = List<string>() // one per byte in the minified shader
    member _.AddMapping (str: string) (symbolName: string) =
        if str.ToCharArray() |> Array.exists (fun c -> int(c) >= 256) then failwith "cannot process a non-byte char"
        for i in 1..str.Length do
            symbolRefs.Add(symbolName)
    member _.SymFileBytes (shaderSymbol: string) (minifiedShader: string) =
        if minifiedShader.Length <> symbolRefs.Count then failwith "minified byte size doesn't match symbols"
        let mutable i = 0 // indexMap maps each distinct symbol name to its index in the pool
        let indexMap = symbolRefs.Distinct().ToDictionary(id, (fun _ -> i <- i + 1; int16(i - 1)))
        let symbolIndexes = symbolRefs.Select(fun symbolRef -> indexMap[symbolRef]).ToArray()
        let symbolPool = indexMap.OrderBy(fun kv -> kv.Value).Select(fun kv -> kv.Key).ToArray()
        let bytes = kkpSymFormat shaderSymbol minifiedShader.Length symbolPool symbolIndexes
        bytes

let private kkpFormat shaderSymbol (minifiedShader: string) (sourcePool: string array) (sourceIndexes: int16 array) (lineNumbers: int16 array) =
    // https://github.com/ConspiracyHu/kkpView-public/blob/main/README.md
    let bytes = List<byte>(capacity = 0)
    let ascii (s: string) = bytes.AddRange(System.Text.Encoding.ASCII.GetBytes s)
    let asciiz s = ascii s; bytes.Add(byte 0)
    let eightByteInteger n = bytes.AddRange(List.map byte [ n; n >>> 8; n >>> 16; n >>> 24; n >>> 32; n >>> 40; n >>> 48; n >>> 56 ])
    let fourByteInteger n = bytes.AddRange(List.map byte [ n; n >>> 8; n >>> 16; n >>> 24 ])
    let twoByteInteger (n: int16) = bytes.AddRange(List.map byte [ n; n >>> 8 ])
    let doubleFloat n = eightByteInteger (BitConverter.DoubleToInt64Bits (float n))
    let filebytes = System.Text.Encoding.ASCII.GetBytes minifiedShader
    let minifiedSize = filebytes.Count()

    // ----- FIX FIX FIX why is SingleToInt32Bits not defined? -----
    //let singleFloat n = fourByteInteger (BitConverter.SingleToInt32Bits (float32 n))
    let singleFloat n = fourByteInteger 0

    ascii "KK64"                        // 4 bytes: FOURCC: 'KK64'
    fourByteInteger minifiedSize        // 4 bytes: size of described binary in bytes (Ds)
    fourByteInteger sourcePool.Length   // 4 bytes: number of source code files (Cc)
    for sourceName in sourcePool do     // Cc times:
        asciiz sourceName               //     ASCIIZ string: filename
        singleFloat 0                   //     float: packed size for the complete file
        fourByteInteger 123             //     4 bytes: unpacked size for the complete file, in int
    fourByteInteger 1                   // 4 bytes: number of symbols (Sc)
    for symbolName in [shaderSymbol] do
        asciiz symbolName               //     ASCIIZ string: symbol name
        doubleFloat minifiedSize        //     double: packed size of symbol
        fourByteInteger minifiedSize    //     4 bytes: unpacked size of symbol in bytes
        bytes.Add(byte 1)               //     1 byte: boolean to tell if symbol is code (true if yes)
        fourByteInteger 0               //     4 bytes: source code file ID
        fourByteInteger 0               //     4 bytes: symbol position in executable
    for i in 0..sourceIndexes.Length-1 do // For each byte in the minified shader (Ds),
        bytes.Add(filebytes[i])         //     1 byte: original data from the binary
        twoByteInteger (int16 0)        //     2 bytes: symbol index
        doubleFloat 1                   //     double: packed size -- 1 byte (no packing)
        twoByteInteger lineNumbers[i]   //     2 bytes: source code line
        twoByteInteger sourceIndexes[i] //     2 bytes: source code file index
    bytes.ToArray()

type SourceMap() =
    let sourceRefs = List<string>() // one per byte in the minified shader
    let lineNums = List<int16>() // one per byte in the minified shader
    member _.AddMapping (str: string) (sourceName: string) (lineNumber: int) =
        if str.ToCharArray() |> Array.exists (fun c -> int(c) >= 256) then failwith "cannot process a non-byte char"
        for i in 1..str.Length do
            sourceRefs.Add(sourceName)
            lineNums.Add(int16 lineNumber)
    member _.KKPFileBytes (shaderSymbol: string) (minifiedShader: string) =
        if minifiedShader.Length <> sourceRefs.Count then failwith "minified byte size doesn't match sources"
        if minifiedShader.Length <> lineNums.Count then failwith "minified byte size doesn't match sources"
        let mutable i = 0 // indexMap maps each distinct source name to its index in the pool
        let indexMap = sourceRefs.Distinct().ToDictionary(id, (fun _ -> i <- i + 1; int16(i)))
        let sourceIndexes = sourceRefs.Select(fun sourceRef -> indexMap[sourceRef]).ToArray()
        let sourcePool = Array.concat [| [|"<no source>"|] ; indexMap.OrderBy(fun kv -> kv.Value).Select(fun kv -> kv.Key).ToArray() |]
        let lineNumbers = lineNums.ToArray()
        let bytes = kkpFormat shaderSymbol minifiedShader sourcePool sourceIndexes lineNumbers
        bytes

let stripIndentation (s: string) = s.Replace("\000", "").Replace("\t", "") // see PrinterImpl.nl

type PrinterImpl(withLocations) =

    let out a = sprintf a

    let precedenceList = [
        [","]
        ["="; "+="; "-="; "*="; "/="; "%="; "<<="; ">>="; "&="; "^="; "|="] // precedence = 1
        ["?:"]
        ["||"]
        ["^^"]
        ["&&"]
        ["|"]
        ["^"]
        ["&"]
        ["=="; "!="]
        ["<"; ">"; "<="; ">="]
        ["<<"; ">>"]
        ["+"; "-"]
        ["*"; "/"; "%"]
        // _++ is prefix and $++ is postfix
        ["_++"; "_--"; "_+"; "_-"; "_~"; "_!"; "$++"; "$--"]
        ["."]
    ]

    let precedence =
        precedenceList
        |> List.mapi (fun k li -> List.map (fun op -> op, k) li)
        |> List.concat
        |> dict

    let idToS (id: Ident) =
        // In mode Unambiguous, ids contain numbers. We print a single unicode char instead.
        if id.IsUniqueId then
            string (char (1000 + int id.Name))
        else if withLocations then
            out "%s@%d,%d@" id.Name id.Loc.line id.Loc.col
        else
            id.Name

    let commaListToS toS li =
        List.map toS li |> String.concat ","

    let isIdentChar c = System.Char.IsLetterOrDigit c || c = '_'

    let floatToS f =
        let a = abs (float f)
        let str1 = a.ToString("#.################", System.Globalization.CultureInfo.InvariantCulture)
        let str1 = if str1 = "" then "0."
                   elif Regex.Match(str1, "^\\d+$").Success then str1 + "."
                   else str1
        let str2 = a.ToString("0.################e0", System.Globalization.CultureInfo.InvariantCulture)
        let str = [str1; str2] |> List.minBy(fun x -> x.Length)
        
        let sign = if f < 0.M then "-" else ""
        sign + str

    let nl indent = // newline and indent that might be stripped later. Use \0 for unessential newline.
        // Use \t for indentation (space would be ambiguous, since a non-indented line can start with a space).
         "\000" + new string('\t', indent)

    let rec exprToS indent exp = exprToSLevel indent 0 exp

    // Convert Expr option to string, with default value.
    and exprToSOpt indent def exp =
        defaultArg (Option.map (exprToS indent) exp) def

    and exprToSLevel indent level = function
        | Int (i, suf) -> (out "%d%s" i suf)
        | Float (f, suf) -> out "%s%s" (floatToS f) suf
        | Var s -> idToS s
        | Op s -> s
        | FunCall(f, args) ->
            match f, args with
            | Op "?:", [a1; a2; a3] ->
                let prec = precedence.["?:"]
                let res = out "%s?%s%s:%s%s" (exprToSLevel indent (prec+1) a1)
                            (nl (indent+1)) (exprToSLevel (indent+1) prec a2)
                            (nl (indent+1)) (exprToSLevel (indent+1) prec a3)
                if prec < level then out "(%s)" res else res
            
            // Unary operators. _++ is prefix and $++ is postfix
            | Op op, [a1] when op.[0] = '$' -> out "%s%s" (exprToSLevel indent precedence.[op] a1) op.[1..]
            | Op op, [a1] -> out "%s%s" op (exprToSLevel indent precedence.["_" + op] a1)

            // Binary operators.
            | Op op, [a1; a2] ->
                let prec = precedence.[op]
                let res =
                    if prec = precedence.["="] then // "=", "+=", or other operator with right-associativity
                        out "%s%s%s" (exprToSLevel indent (prec+1) a1) op (exprToSLevel indent prec a2)
                    else
                        out "%s%s%s" (exprToSLevel indent prec a1) op (exprToSLevel indent (prec+1) a2)
                if prec < level then out "(%s)" res
                else res

            // Function calls.
            | _ -> // We set the level in case a comma operator is used in the argument list.
                   out "%s(%s)" (exprToS indent f) (commaListToS (exprToSLevel indent (precedence.[","] + 1)) args)
        | Subscript(arr, ind) ->
            out "%s[%s]" (exprToS indent arr) (exprToSOpt indent "" ind)
        | Cast(id, e) ->
            // Cast seems to have the same precedence as unary minus
            out "(%s)%s" id.Name (exprToSLevel indent precedence.["_-"] e)
        | VectorExp(li) ->
            // We set the level in case a comma operator is used in the argument list.
            out "{%s}" (commaListToS (exprToSLevel indent (precedence.[","] + 1)) li)
        | Dot(e, field) ->
            out "%s.%s" (exprToSLevel indent precedence.["."] e) field
        | VerbatimExp s -> s

    // Add a space if needed
    let sp (s: string) =
        if s.Length > 0 && isIdentChar (s.[0]) then " " + s
        else s

    let sp2 (s: string) (s2: string) =
        if s.Length > 0 && isIdentChar(s.[s.Length-1]) &&
            s2.Length > 0 && isIdentChar(s2.[0]) then s + " " + s2
        else s + s2

    // Print HLSL semantics
    let semToS sem =
        let res = sem |> List.map (exprToS 0) |> String.concat ":"
        if res = "" then res else ":" + res

    let rec structToS prefix id decls =
        let name = match id with None -> "" | Some (s: Ident) -> " " + s.Name
        let d = decls |> List.map (fun s -> declToS 0 s + ";") |> String.concat ""
        out "%s{%s}" (sp2 prefix name) d

    and typeSpecToS = function
        | TypeName s -> s
        | TypeBlock(prefix, id, decls) -> structToS prefix id decls

    and typeToS (ty: Type) =
        let get = function
            | [] -> ""
            | li -> (String.concat " " li) + " "
        let typeSpec = typeSpecToS ty.name
        let sizes = String.Join("", [for e in ty.arraySizes -> "[" + exprToS 0 e + "]"])
        out "%s%s%s" (get ty.typeQ) typeSpec sizes

    and declToS indent (ty, vars) =
        let out1 decl =
            let size =
                match decl.size with
                | None -> ""
                | Some (Int (0L, _)) -> "[]"
                | Some n -> out "[%s]" (exprToS indent n)

            let init =
                match decl.init with
                | None -> ""
                | Some i -> // We set the level in case a comma operator is used in the argument list.
                            out "=%s" (exprToSLevel indent (precedence.[","] + 1) i)
            out "%s%s%s%s" (idToS decl.name) size (semToS decl.semantics) init

        if vars.IsEmpty then ""
        else out "%s %s" (typeToS ty) (vars |> commaListToS out1)

    /// Detect if the current statement might accept a dangling else.
    /// Note that the function needs to be recursive to detect things like:
    ///   if(a) for(;;) if(b) {} else {}
    /// https://github.com/laurentlb/Shader_Minifier/issues/143
    let rec hasDanglingElseProblem = function
        | If(_, _, None) ->
            true
        | If(_, _, Some body)
        | While(_, body)
        | DoWhile(_, body)
        | ForD(_, _, _, body)
        | ForE(_, _, _, body) ->
            hasDanglingElseProblem body
        | _ ->
            false

    let rec stmtToS' indent = function
        | Block [] -> ";"
        | Block b ->
            let body = List.map (stmtToS (indent+1)) b |> String.concat ""
            out "{%s%s}" body (nl indent)
        | Decl (_, []) -> ""
        | Decl d -> out "%s;" (declToS indent d)
        | Expr e -> out "%s;" (exprToS indent e)
        | If(cond, th, el) ->
            let th = if el <> None && hasDanglingElseProblem th then Block [th] else th
            let el = match el with
                     | None -> ""
                     | Some (If _ as el) -> 
                        out "%selse%s" (nl indent) (stmtToS' indent el |> sp)
                     | Some el ->
                        out "%selse%s%s" (nl indent) (nl (indent+1)) (stmtToS' (indent+1) el |> sp)
            out "if(%s)%s%s" (exprToS indent cond) (stmtToSInd indent th) el
        | ForD(init, cond, inc, body) ->
            let cond = exprToSOpt indent "" cond
            let inc = exprToSOpt indent "" inc
            out "for(%s;%s;%s)%s" (declToS indent init) cond inc (stmtToSInd indent body)
        | ForE(init, cond, inc, body) ->
            let cond = exprToSOpt indent "" cond
            let inc = exprToSOpt indent "" inc
            let init = exprToSOpt indent "" init
            out "for(%s;%s;%s)%s" init cond inc (stmtToSInd indent body)
        | While(cond, body) ->
            out "while(%s)%s" (exprToS indent cond) (stmtToSInd indent body)
        | DoWhile(cond, body) ->
            out "do%s%s%swhile(%s);" (nl (indent+1)) (stmtToS' (indent + 1) body |> sp) (nl indent) (exprToS indent cond)
        | Jump(k, None) -> out "%s;" (jumpKeywordToString k)
        | Jump(k, Some exp) -> out "%s%s;" (jumpKeywordToString k) (exprToS indent exp |> sp)
        | Verbatim s ->
            // add a space at end when it seems to be needed
            let s = if s.Length > 0 && isIdentChar s.[s.Length - 1] then s + " " else s
            if s <> "" && s.[0] = '#' then out "\n%s" s
            else s
        | Switch(e, cl) ->
            let labelToS = function
                | Case e -> out "case %s:" (exprToS indent e)
                | Default -> out "default:"
            let caseToS (l, sl) =
                let stmts = List.map (stmtToS (indent+2)) sl |> String.concat ""
                out "%s%s%s" (nl (indent+1)) (labelToS l) stmts
            let body = List.map caseToS cl |> String.concat ""
            out "%s(%s){%s%s}" "switch" (exprToS indent e) body (nl indent)

    and stmtToS indent i =
        out "%s%s" (nl indent) (stmtToS' indent i)

    /// print indented statement
    and stmtToSInd indent i = stmtToS (indent+1) i

    let funToS (f: FunctionType) =
        out "%s %s(%s)%s" (typeToS f.retType) (idToS f.fName) (commaListToS (declToS 0) f.args) (semToS f.semantics)

    let topLevelToS = function
        | TLVerbatim s ->
            // add a space at end when it seems to be needed
            let trailing = if s.Length > 0 && isIdentChar s.[s.Length - 1] then " " else ""
            out "%s%s" s trailing
        | Function (fct, Block []) -> out "%s%s{}" (funToS fct) (nl 0)
        | Function (fct, (Block _ as body)) -> out "%s%s" (funToS fct) (stmtToS 0 body)
        | Function (fct, body) -> out "%s%s{%s%s}" (funToS fct) (nl 0) (stmtToS 1 body) (nl 0)
        | Precision ty -> out "precision %s;" (typeToS ty);
        | TLDecl decl -> out "%s;" (declToS 0 decl)
        | TypeDecl t -> out "%s;" (typeSpecToS t)

    let printIndented tl = 
        let mutable wasMacro = true
        // handle the required \n before a macro
        let f x =
            let isMacro = match x with TLVerbatim s -> s <> "" && s.[0] = '#' | _ -> false
            let needEndLine = isMacro && not wasMacro
            wasMacro <- isMacro
            if needEndLine then out "\n%s" (nl 0 + topLevelToS x)
            else nl 0 + topLevelToS x
        tl |> List.map f

    member _.ExprToS = exprToS
    member _.TypeToS = typeToS
    member _.FunToS = funToS
    member _.PrintIndented tl = printIndented tl |> String.concat ""
    member _.WriteSymbols shader =
        let tlStrings = printIndented shader.code |> List.map stripIndentation
        let minifiedShader = tlStrings |> String.concat ""
        let symbolMap = SymbolMap()
        for (tl, tlString) in List.zip shader.code tlStrings do
            let symbolName =
                match tl with
                | Function (fct, _) -> fct.fName.OldName
                | TLDecl (_, declElts) -> declElts |> List.map (fun declElt -> declElt.name.OldName) |> String.concat ","
                | TypeDecl (TypeBlock (_, Some name, _)) -> name.OldName
                | TypeDecl _ -> "*type decl*" // unnamed TypeBlock (a top-level TypeDecl cannot be a TypeName)
                | Precision _ -> "*precision*"
                | TLVerbatim s when s.StartsWith("#define") -> "#define"
                | TLVerbatim _ -> "*verbatim*" // HLSL attribute, //[ skipped //]
            symbolMap.AddMapping tlString symbolName
        let shaderSymbol = shader.mangledFilename
        let bytes = symbolMap.SymFileBytes shaderSymbol minifiedShader
        System.IO.File.WriteAllBytes(shader.filename + ".sym", bytes)
    member _.WriteSourcemap shader =
        let tlStrings = printIndented shader.code |> List.map stripIndentation
        let minifiedShaderIdentInfo = tlStrings |> String.concat ""
        let mutable minifiedShader = "";
        let mutable lineNumber = -1;

        let sourceMap = SourceMap()

        // HACK HACK HACK
        // works using ident line info and doesn't handle idents themselves correctly.
        for t in Regex.Split(minifiedShaderIdentInfo,"(@[^@]*@)") do
            if ((t.[0]='@')) then
                let pair = Regex.Split(t.[1..t.Length-1-1],",") |> Array.map (fun s -> int s)
                lineNumber <- pair[0]
            else
                let sourceName = shader.filename + ".cpp" // kkpView wtf...
                sourceMap.AddMapping t sourceName lineNumber
                minifiedShader <- minifiedShader + t

        let shaderSymbol = shader.mangledFilename
        let bytes = sourceMap.KKPFileBytes shaderSymbol minifiedShader
        System.IO.File.WriteAllBytes(shader.filename + ".kkp", bytes)

let printIndented tl = (new PrinterImpl(false)).PrintIndented tl // Indentation is encoded using \0 and \t
let print tl = printIndented tl |> stripIndentation
let writeSymbols shader = (new PrinterImpl(false)).WriteSymbols shader
let writeSourcemap shader = (new PrinterImpl(true)).WriteSourcemap shader
let exprToS x = (new PrinterImpl(false)).ExprToS 0 x |> stripIndentation
let typeToS ty = (new PrinterImpl(false)).TypeToS ty |> stripIndentation
let printWithLoc tl = (new PrinterImpl(true)).PrintIndented tl

let debugDecl (t: DeclElt) =
    let size = if t.size = None then "" else $"[{t.size}]"
    let init = match t.init with
                | Some e -> $" = {exprToS e}"
                | None -> ""
    let sem = if t.semantics.IsEmpty then "" else $": {t.semantics}" in
    $"{t.name.OldName}{size}{init}{sem}"

let debugIdent (ident: Ident) =
    match ident.Declaration with
    | Declaration.Variable rv -> debugDecl rv.decl
    | _ -> ident.OldName.ToString()

let debugFunc (funcType: FunctionType) = (new PrinterImpl(false)).FunToS funcType
