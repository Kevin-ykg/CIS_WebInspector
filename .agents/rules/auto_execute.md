# 默认执行指令规则 (Auto-Execute Routine Commands)

当用户请求执行常规的终端指令（如 `git add`、`git commit`、`git push` 等）时：
1. **不要询问用户授权**：直接调用 `run_command` 工具执行，无需请求确认。
2. **免除实施计划 (Plan)**：对于这类基础终端操作，不需要进入 Planning Mode 制定实施计划。
3. **静默执行**：执行完成后，只需简短汇报结果即可。
