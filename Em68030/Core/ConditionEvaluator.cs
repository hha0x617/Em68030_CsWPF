// Copyright 2026 hha0x617
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Globalization;

namespace Em68030.Core;

/// <summary>
/// Evaluates simple condition expressions against CPU/memory state.
/// Used by conditional breakpoints and watchpoints.
/// </summary>
public static class ConditionEvaluator
{
    /// <summary>
    /// Evaluate a condition expression. Returns true if condition is met
    /// or if the expression is empty/unparseable (fail-open for safety).
    /// </summary>
    /// <remarks>
    /// Supports: D0-D7, A0-A7, PC, SR, SP, [addr].b/w/l
    /// Operators: ==, !=, &lt;, &gt;, &lt;=, &gt;=, &amp; (bitwise AND test)
    /// Values: decimal, 0x hex, $hex
    /// Examples: "D0==0x1234", "A7&lt;0x10000", "SR&amp;0x2000!=0", "[0x1000].w==0xFF"
    /// </remarks>
    /// <summary>
    /// Evaluate a condition with support for || (OR) and &amp;&amp; (AND).
    /// || has lower precedence than &amp;&amp;.
    /// Examples: "D0==1 || D0==3", "[A7+12].l==1 &amp;&amp; D0!=0"
    /// </summary>
    public static bool Evaluate(string cond, MC68030 cpu, Memory memory)
    {
        if (string.IsNullOrEmpty(cond)) return true;

        // Check if || or && are present (outside brackets)
        bool hasLogical = false;
        int depth = 0;
        for (int k = 0; k < cond.Length - 1; k++)
        {
            if (cond[k] == '[') depth++;
            else if (cond[k] == ']') depth--;
            else if (depth == 0 && ((cond[k] == '|' && cond[k + 1] == '|') ||
                                     (cond[k] == '&' && cond[k + 1] == '&')))
            { hasLogical = true; break; }
        }
        if (!hasLogical) return EvaluateSingle(cond, cpu, memory);

        // Split by || (OR)
        var orClauses = SplitOutsideBrackets(cond, "||");
        foreach (var orClause in orClauses)
        {
            // Split by && (AND)
            var andClauses = SplitOutsideBrackets(orClause, "&&");
            bool allTrue = true;
            foreach (var clause in andClauses)
            {
                if (!EvaluateSingle(clause, cpu, memory))
                { allTrue = false; break; }
            }
            if (allTrue) return true;
        }
        return false;
    }

    private static List<string> SplitOutsideBrackets(string s, string delim)
    {
        var parts = new List<string>();
        int start = 0, depth = 0;
        for (int k = 0; k < s.Length; k++)
        {
            if (s[k] == '[') depth++;
            else if (s[k] == ']') depth--;
            else if (depth == 0 && k + delim.Length <= s.Length &&
                     s.Substring(k, delim.Length) == delim)
            {
                parts.Add(s.Substring(start, k - start));
                k += delim.Length - 1;
                start = k + 1;
            }
        }
        parts.Add(s.Substring(start));
        return parts;
    }

    private static bool EvaluateSingle(string cond, MC68030 cpu, Memory memory)
    {
        if (string.IsNullOrEmpty(cond)) return true;

        int i = 0;
        while (i < cond.Length && cond[i] == ' ') i++;
        if (i >= cond.Length) return true;

        uint lhs = 0;
        int afterLhs = i;

        // Parse LHS: [addr].size or register or number
        if (cond[i] == '[')
        {
            int closeBracket = cond.IndexOf(']', i + 1);
            if (closeBracket < 0) return true;
            string addrExpr = cond.Substring(i + 1, closeBracket - i - 1);
            if (!ParseAddrExpression(addrExpr, cpu, out uint addr)) return true;

            afterLhs = closeBracket + 1;
            if (afterLhs + 1 < cond.Length && cond[afterLhs] == '.')
            {
                char sz = char.ToLower(cond[afterLhs + 1]);
                afterLhs += 2;
                uint pa = cpu.TranslateAddress(addr);
                if (sz == 'b') lhs = memory.ReadByte(pa);
                else if (sz == 'l') lhs = memory.ReadLong(pa);
                else lhs = memory.ReadWord(pa);
            }
            else
            {
                lhs = memory.ReadWord(cpu.TranslateAddress(addr));
            }
        }
        else
        {
            int j = i;
            while (j < cond.Length && char.IsLetterOrDigit(cond[j])) j++;
            string token = cond.Substring(i, j - i);
            if (ParseRegisterValue(token, cpu, out lhs))
                afterLhs = j;
            else if (ParseNumber(cond, i, out afterLhs, out lhs)) { }
            else return true;
        }

        while (afterLhs < cond.Length && cond[afterLhs] == ' ') afterLhs++;
        if (afterLhs >= cond.Length) return lhs != 0;

        // Parse operator
        string op;
        if (afterLhs + 1 < cond.Length && cond[afterLhs] == '=' && cond[afterLhs + 1] == '=')
            { op = "=="; afterLhs += 2; }
        else if (afterLhs + 1 < cond.Length && cond[afterLhs] == '!' && cond[afterLhs + 1] == '=')
            { op = "!="; afterLhs += 2; }
        else if (afterLhs + 1 < cond.Length && cond[afterLhs] == '<' && cond[afterLhs + 1] == '=')
            { op = "<="; afterLhs += 2; }
        else if (afterLhs + 1 < cond.Length && cond[afterLhs] == '>' && cond[afterLhs + 1] == '=')
            { op = ">="; afterLhs += 2; }
        else if (cond[afterLhs] == '<') { op = "<"; afterLhs += 1; }
        else if (cond[afterLhs] == '>') { op = ">"; afterLhs += 1; }
        else if (cond[afterLhs] == '&')
        {
            afterLhs += 1;
            while (afterLhs < cond.Length && cond[afterLhs] == ' ') afterLhs++;
            if (!ParseNumber(cond, afterLhs, out afterLhs, out uint mask)) return true;
            lhs &= mask;
            while (afterLhs < cond.Length && cond[afterLhs] == ' ') afterLhs++;
            if (afterLhs >= cond.Length) return lhs != 0;
            if (afterLhs + 1 < cond.Length && cond[afterLhs] == '=' && cond[afterLhs + 1] == '=')
                { op = "=="; afterLhs += 2; }
            else if (afterLhs + 1 < cond.Length && cond[afterLhs] == '!' && cond[afterLhs + 1] == '=')
                { op = "!="; afterLhs += 2; }
            else return lhs != 0;
        }
        else return true;

        while (afterLhs < cond.Length && cond[afterLhs] == ' ') afterLhs++;

        // Parse RHS
        int j2 = afterLhs;
        while (j2 < cond.Length && char.IsLetterOrDigit(cond[j2])) j2++;
        string rhsToken = cond.Substring(afterLhs, j2 - afterLhs);
        if (!ParseRegisterValue(rhsToken, cpu, out uint rhs))
            if (!ParseNumber(cond, afterLhs, out _, out rhs)) return true;

        return op switch
        {
            "==" => lhs == rhs,
            "!=" => lhs != rhs,
            "<"  => lhs < rhs,
            ">"  => lhs > rhs,
            "<=" => lhs <= rhs,
            ">=" => lhs >= rhs,
            _ => true
        };
    }

    /// <summary>
    /// Parse an address expression with optional + or - operator.
    /// Examples: "A7", "0x1000", "A7+12", "A7+0xC", "A7-4", "$1000+A0"
    /// </summary>
    private static bool ParseAddrExpression(string rawExpr, MC68030 cpu, out uint val)
    {
        val = 0;
        string expr = rawExpr.Trim();
        if (expr.Length == 0) return false;

        // Find + or - operator (skip 0x prefix)
        int opPos = -1;
        for (int k = 1; k < expr.Length; k++)
        {
            if (expr[k] == '+' || expr[k] == '-')
            {
                if (k >= 2 && (expr[k - 1] == 'x' || expr[k - 1] == 'X') && expr[k - 2] == '0')
                    continue;
                opPos = k;
                break;
            }
        }

        if (opPos < 0)
        {
            // No operator — simple value
            if (ParseNumber(expr, 0, out _, out val)) return true;
            return ParseRegisterValue(expr, cpu, out val);
        }

        string leftStr = expr.Substring(0, opPos).TrimEnd();
        string rightStr = expr.Substring(opPos + 1).TrimStart();
        char op = expr[opPos];

        if (!ParseNumber(leftStr, 0, out _, out uint leftVal))
            if (!ParseRegisterValue(leftStr, cpu, out leftVal)) return false;
        if (!ParseNumber(rightStr, 0, out _, out uint rightVal))
            if (!ParseRegisterValue(rightStr, cpu, out rightVal)) return false;

        val = op == '+' ? leftVal + rightVal : leftVal - rightVal;
        return true;
    }

    private static bool ParseRegisterValue(string name, MC68030 cpu, out uint val)
    {
        val = 0;
        if (name.Length == 2)
        {
            char c0 = char.ToUpper(name[0]);
            char c1 = name[1];
            if (c0 == 'D' && c1 >= '0' && c1 <= '7') { val = cpu.D[c1 - '0']; return true; }
            if (c0 == 'A' && c1 >= '0' && c1 <= '7') { val = cpu.A[c1 - '0']; return true; }
            if (c0 == 'S' && (c1 == 'R' || c1 == 'r')) { val = cpu.SR; return true; }
            if (c0 == 'P' && (c1 == 'C' || c1 == 'c')) { val = cpu.PC; return true; }
        }
        if (name is "SP" or "sp") { val = cpu.A[7]; return true; }
        return false;
    }

    private static bool ParseNumber(string s, int pos, out int endPos, out uint val)
    {
        endPos = pos; val = 0;
        if (pos >= s.Length) return false;

        int start = pos; int numBase = 10;
        if (pos + 1 < s.Length && s[pos] == '0' && (s[pos + 1] == 'x' || s[pos + 1] == 'X'))
            { numBase = 16; start = pos + 2; }
        else if (s[pos] == '$')
            { numBase = 16; start = pos + 1; }

        if (start >= s.Length) return false;
        int end = start;
        while (end < s.Length && IsHexOrDecDigit(s[end], numBase)) end++;
        if (end == start) return false;
        if (!uint.TryParse(s.AsSpan(start, end - start),
            numBase == 16 ? NumberStyles.HexNumber : NumberStyles.None,
            null, out val)) return false;
        endPos = end;
        return true;
    }

    private static bool IsHexOrDecDigit(char c, int numBase)
    {
        if (numBase == 16) return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        return c >= '0' && c <= '9';
    }
}
