namespace ShaderExplorer.Decompiler.Dxil;

/// <summary>
///     Recovers structured control flow (if/else, loops) from a DXIL function's CFG.
/// </summary>
public class ControlFlowRecovery
{
    /// <summary>
    ///     Attempts to recover structured control flow for a function.
    ///     Falls back to labeled blocks with gotos for irreducible graphs.
    /// </summary>
    public CfNode Recover(DxilFunction func)
    {
        if (func.BasicBlocks.Count == 0)
            return new SequenceNode();

        if (func.BasicBlocks.Count == 1)
            return new BlockNode { Block = func.BasicBlocks[0] };

        var blockMap = new Dictionary<string, DxilBasicBlock>();
        foreach (var bb in func.BasicBlocks)
            blockMap[bb.Label] = bb;

        // Compute dominance and detect back edges for loop detection
        var visited = new HashSet<string>();
        var backEdges = new HashSet<(string from, string to)>();
        DetectBackEdges(func.BasicBlocks[0].Label, blockMap, visited, new HashSet<string>(), backEdges);

        var loopHeaders = new HashSet<string>(backEdges.Select(e => e.to));

        return RecoverRegion(func.BasicBlocks[0].Label, blockMap, loopHeaders, backEdges, new HashSet<string>(), null);
    }

    private CfNode RecoverRegion(
        string startLabel,
        Dictionary<string, DxilBasicBlock> blockMap,
        HashSet<string> loopHeaders,
        HashSet<(string from, string to)> backEdges,
        HashSet<string> visited,
        string? exitLabel)
    {
        var sequence = new SequenceNode();

        var current = startLabel;
        while (current != null && !visited.Contains(current))
        {
            if (current == exitLabel)
                break;

            if (!blockMap.TryGetValue(current, out var block))
                break;

            visited.Add(current);

            // Loop header
            if (loopHeaders.Contains(current))
            {
                var loopBackEdge = backEdges.FirstOrDefault(e => e.to == current);
                var loopExit = FindLoopExit(block, blockMap, current, visited);

                var loopBody = new SequenceNode();
                loopBody.Children.Add(new BlockNode { Block = block });

                // Process the loop body — simplified: just add block and handle terminator
                var loopNode = new LoopNode
                {
                    HeaderLabel = current,
                    ExitLabel = loopExit ?? "",
                    Body = loopBody
                };

                sequence.Children.Add(loopNode);

                if (block.Terminator != null)
                    ProcessTerminatorInLoop(block, loopNode, loopBody, blockMap, loopHeaders, backEdges, visited,
                        current, loopExit);

                current = loopExit;
                continue;
            }

            sequence.Children.Add(new BlockNode { Block = block });

            // Handle terminator
            if (block.Terminator == null)
            {
                current = null;
                continue;
            }

            switch (block.Terminator.Kind)
            {
                case DxilTerminatorKind.Branch:
                    current = block.Terminator.TargetLabel;
                    break;

                case DxilTerminatorKind.ConditionalBranch:
                {
                    var trueLabel = block.Terminator.TrueLabel;
                    var falseLabel = block.Terminator.FalseLabel;

                    if (trueLabel == null || falseLabel == null)
                    {
                        current = trueLabel ?? falseLabel;
                        break;
                    }

                    // Find the merge point (immediate post-dominator)
                    var merge = FindMergePoint(trueLabel, falseLabel, blockMap, visited);

                    var ifNode = new IfNode
                    {
                        Condition = block.Terminator.Condition!,
                        MergeLabel = merge ?? ""
                    };

                    var thenVisited = new HashSet<string>(visited);
                    ifNode.ThenBody = RecoverRegion(trueLabel, blockMap, loopHeaders, backEdges, thenVisited, merge);

                    if (falseLabel != merge)
                    {
                        var elseVisited = new HashSet<string>(visited);
                        foreach (var v in thenVisited) elseVisited.Add(v);
                        ifNode.ElseBody = RecoverRegion(falseLabel, blockMap, loopHeaders, backEdges, elseVisited,
                            merge);
                        foreach (var v in elseVisited) visited.Add(v);
                    }

                    foreach (var v in thenVisited) visited.Add(v);

                    sequence.Children.Add(ifNode);
                    current = merge;
                    break;
                }

                case DxilTerminatorKind.Return:
                case DxilTerminatorKind.Unreachable:
                    current = null;
                    break;

                case DxilTerminatorKind.Switch:
                {
                    var sw = block.Terminator;
                    if (sw.SwitchValue == null)
                    {
                        current = null;
                        break;
                    }

                    // Collect all target labels
                    var caseTargets = sw.SwitchCases.Select(c => c.Label).ToList();
                    var defaultTarget = sw.DefaultLabel;
                    var allTargets = new List<string>(caseTargets);
                    if (defaultTarget != null) allTargets.Add(defaultTarget);

                    // Find merge point: first block reachable from all targets
                    var merge = FindSwitchMerge(allTargets, blockMap);

                    var switchNode = new SwitchNode
                    {
                        SwitchValue = sw.SwitchValue,
                        MergeLabel = merge ?? ""
                    };

                    // Recover each case body
                    var caseVisited = new HashSet<string>(visited);
                    foreach (var (val, label) in sw.SwitchCases)
                    {
                        if (!caseVisited.Contains(label) && blockMap.ContainsKey(label))
                        {
                            var caseBody = RecoverRegion(label, blockMap, loopHeaders, backEdges,
                                new HashSet<string>(caseVisited), merge);
                            switchNode.Cases.Add((val, caseBody));
                        }
                        else
                        {
                            switchNode.Cases.Add((val, new GotoNode { TargetLabel = label }));
                        }
                    }

                    // Recover default case body
                    if (defaultTarget != null && !caseVisited.Contains(defaultTarget) &&
                        blockMap.ContainsKey(defaultTarget))
                    {
                        switchNode.DefaultBody = RecoverRegion(defaultTarget, blockMap, loopHeaders, backEdges,
                            new HashSet<string>(caseVisited), merge);
                    }

                    // Mark all case targets as visited (they've been recovered in sub-regions)
                    foreach (var label in allTargets)
                        visited.Add(label);

                    sequence.Children.Add(switchNode);
                    current = merge;
                    break;
                }

                default:
                    current = null;
                    break;
            }
        }

        if (sequence.Children.Count == 1)
            return sequence.Children[0];

        return sequence;
    }

    private void ProcessTerminatorInLoop(
        DxilBasicBlock header,
        LoopNode loopNode,
        SequenceNode loopBody,
        Dictionary<string, DxilBasicBlock> blockMap,
        HashSet<string> loopHeaders,
        HashSet<(string from, string to)> backEdges,
        HashSet<string> visited,
        string headerLabel,
        string? exitLabel)
    {
        if (header.Terminator == null) return;

        switch (header.Terminator.Kind)
        {
            case DxilTerminatorKind.ConditionalBranch:
            {
                // One branch is loop body, one is exit
                var trueLabel = header.Terminator.TrueLabel;
                var falseLabel = header.Terminator.FalseLabel;

                var bodyLabel = trueLabel == exitLabel ? falseLabel : trueLabel;

                if (bodyLabel != null && blockMap.ContainsKey(bodyLabel) && !visited.Contains(bodyLabel))
                {
                    var bodyNode = RecoverRegion(bodyLabel, blockMap, loopHeaders, backEdges, visited,
                        exitLabel ?? headerLabel);
                    loopBody.Children.Add(bodyNode);
                }

                break;
            }
            case DxilTerminatorKind.Branch:
            {
                var target = header.Terminator.TargetLabel;
                if (target != null && target != headerLabel && target != exitLabel &&
                    blockMap.ContainsKey(target) && !visited.Contains(target))
                {
                    var bodyNode = RecoverRegion(target, blockMap, loopHeaders, backEdges, visited,
                        exitLabel ?? headerLabel);
                    loopBody.Children.Add(bodyNode);
                }

                break;
            }
        }
    }

    private static void DetectBackEdges(
        string current,
        Dictionary<string, DxilBasicBlock> blockMap,
        HashSet<string> visited,
        HashSet<string> stack,
        HashSet<(string from, string to)> backEdges)
    {
        visited.Add(current);
        stack.Add(current);

        if (blockMap.TryGetValue(current, out var block))
            foreach (var succ in block.Successors)
                if (stack.Contains(succ))
                    backEdges.Add((current, succ));
                else if (!visited.Contains(succ))
                    DetectBackEdges(succ, blockMap, visited, stack, backEdges);

        stack.Remove(current);
    }

    private static string? FindLoopExit(
        DxilBasicBlock header,
        Dictionary<string, DxilBasicBlock> blockMap,
        string headerLabel,
        HashSet<string> visited)
    {
        if (header.Terminator?.Kind == DxilTerminatorKind.ConditionalBranch)
        {
            // One of the branches should be the exit
            if (header.Terminator.TrueLabel != null &&
                !IsReachableWithout(header.Terminator.TrueLabel, headerLabel, blockMap))
                return header.Terminator.TrueLabel;
            if (header.Terminator.FalseLabel != null &&
                !IsReachableWithout(header.Terminator.FalseLabel, headerLabel, blockMap))
                return header.Terminator.FalseLabel;

            // Heuristic: the false branch is usually the exit
            return header.Terminator.FalseLabel;
        }

        return null;
    }

    private static bool IsReachableWithout(string target, string avoid, Dictionary<string, DxilBasicBlock> blockMap)
    {
        // Simple check: can we reach 'avoid' from 'target' without going through 'avoid' itself?
        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(target);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == avoid) return true;
            if (!visited.Add(current)) continue;
            if (blockMap.TryGetValue(current, out var block))
                foreach (var succ in block.Successors)
                    if (!visited.Contains(succ))
                        queue.Enqueue(succ);
        }

        return false;
    }

    private static string? FindSwitchMerge(List<string> targets, Dictionary<string, DxilBasicBlock> blockMap)
    {
        if (targets.Count == 0) return null;
        if (targets.Count == 1) return null;

        // BFS from each target, find first block reachable from ALL targets
        var reachableSets = targets.Select(t => CollectReachable(t, blockMap, 30)).ToList();

        // Intersection of all reachable sets
        var common = new HashSet<string>(reachableSets[0]);
        for (var i = 1; i < reachableSets.Count; i++)
            common.IntersectWith(reachableSets[i]);

        // Remove the targets themselves from candidates
        foreach (var t in targets)
            common.Remove(t);

        if (common.Count == 0) return null;

        // Return the closest common block (lowest BFS depth from first target)
        var firstReach = CollectReachableOrdered(targets[0], blockMap, 30);
        return firstReach.FirstOrDefault(common.Contains);
    }

    private static List<string> CollectReachableOrdered(string start, Dictionary<string, DxilBasicBlock> blockMap,
        int maxDepth)
    {
        var ordered = new List<string>();
        var visited = new HashSet<string>();
        var queue = new Queue<(string label, int depth)>();
        queue.Enqueue((start, 0));

        while (queue.Count > 0)
        {
            var (label, depth) = queue.Dequeue();
            if (depth > maxDepth || !visited.Add(label)) continue;
            ordered.Add(label);

            if (blockMap.TryGetValue(label, out var block))
                foreach (var succ in block.Successors)
                    queue.Enqueue((succ, depth + 1));
        }

        return ordered;
    }

    private static string? FindMergePoint(string trueLabel, string falseLabel,
        Dictionary<string, DxilBasicBlock> blockMap, HashSet<string> visited)
    {
        // Simple heuristic: the first block reachable from both branches
        var trueReachable = CollectReachable(trueLabel, blockMap, 20);
        var falseReachable = CollectReachable(falseLabel, blockMap, 20);

        // Find first common block (BFS order from true branch)
        foreach (var label in trueReachable)
            if (falseReachable.Contains(label))
                return label;

        // Fallback: one branch might directly be the merge point
        if (falseReachable.Contains(trueLabel))
            return trueLabel;
        if (trueReachable.Contains(falseLabel))
            return falseLabel;

        return null;
    }

    private static HashSet<string> CollectReachable(string start, Dictionary<string, DxilBasicBlock> blockMap,
        int maxDepth)
    {
        var reachable = new HashSet<string>();
        var queue = new Queue<(string label, int depth)>();
        queue.Enqueue((start, 0));

        while (queue.Count > 0)
        {
            var (label, depth) = queue.Dequeue();
            if (depth > maxDepth || !reachable.Add(label)) continue;

            if (blockMap.TryGetValue(label, out var block))
                foreach (var succ in block.Successors)
                    queue.Enqueue((succ, depth + 1));
        }

        return reachable;
    }

    /// <summary>
    ///     Represents a structured control flow node.
    /// </summary>
    public abstract class CfNode
    {
    }

    public class SequenceNode : CfNode
    {
        public List<CfNode> Children { get; set; } = [];
    }

    public class BlockNode : CfNode
    {
        public DxilBasicBlock Block { get; set; } = null!;
    }

    public class IfNode : CfNode
    {
        public DxilOperand Condition { get; set; } = null!;
        public CfNode ThenBody { get; set; } = null!;
        public CfNode? ElseBody { get; set; }
        public string MergeLabel { get; set; } = string.Empty;
    }

    public class LoopNode : CfNode
    {
        public CfNode Body { get; set; } = null!;
        public string HeaderLabel { get; set; } = string.Empty;
        public string ExitLabel { get; set; } = string.Empty;
    }

    public class GotoNode : CfNode
    {
        public string TargetLabel { get; set; } = string.Empty;
    }

    public class SwitchNode : CfNode
    {
        public DxilOperand SwitchValue { get; set; } = null!;
        public List<(DxilOperand Value, CfNode Body)> Cases { get; set; } = [];
        public CfNode? DefaultBody { get; set; }
        public string MergeLabel { get; set; } = string.Empty;
    }
}