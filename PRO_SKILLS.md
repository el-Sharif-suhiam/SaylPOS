
# Professional Software Engineering Skills for AI Agents
## دليل المهارات الاحترافية للوكلاء في البرمجيات

> هذا الملف مرجع شامل للمهارات الاحترافية لاي وكيل ذكي في تطوير البرمجيات
> يمكن استخدامه مع: Claude, OpenAI GPT, GitHub Copilot, Gemini, etc.

---

## 1. Core Agent Skills (المهارات الاساسية)

### 1.1 Planning and Task Decomposition (التخطيط وتحليل المهام)
- تقسيم المهام الكبيرة لمهام صغيرة قابلة للتنفيذ
- تحديد التبعيات والترتيب الصحيح
- تحديد نقاط الفشل المحتملة مسبقاً

### 1.2 Systematic Thinking (التفكير المنظم)
- فهم المشكلة بالكامل قبل الحل
- تحليل المدخلات والمخرجات
- تحديد الحالات الحدية (Edge Cases)
- اختبار الحلول متعددة المراحل

### 1.3 Code Review (مراجعة الكود)
- فحص المنطق والحماية والاداء
- التحقق من الاختبارات والتوثيق
- التأكد من adherence للكود ستايل
- NEVER approve code without understanding it

### 1.4 Error Handling (معالجة الاخطاء)
- Never swallow exceptions silently
- Log errors with full context
- Return meaningful error messages
- Use appropriate exception types
- Fail fast for invalid states

### 1.5 Type Safety (السلامة في الانواع)
- Always use strongly typed variables
- Never use 'var' for unclear types
- Validate data at boundaries
- Use nullable types appropriately

### 1.6 Atomic Operations (العمليات الذرية)
- Use transactions for multi-step operations
- Implement proper commit/rollback
- Handle concurrency properly
- Use optimistic/pessimistic locking appropriately

---

## 2. Security Skills (مهارات الامان)

### 2.1 Secure Coding Basics (الاساسيات)
- Input Validation - التحقق من جميع المدخلات
- Output Encoding - ترميز المخرجات
- SQL Injection Prevention - استخدم Parameterized queries
- XSS Prevention - ترميز جميع مدخلات المستخدم
- CSRF Prevention - استخدام tokens
- Password Hashing - bcrypt, argon2, PBKDF2

### 2.2 Secrets Management (ادارة الاسرار)
- NEVER commit secrets to version control
- Use environment variables or secret managers
- Rotate secrets regularly
- Different secrets per environment
- Never log secrets or API keys

### 2.3 Authentication and Authorization (المصادقة والصلاحيات)
- Use strong password hashing (bcrypt recommended)
- Implement MFA when possible
- Use OAuth2/OIDC for external auth
- Principle of Least Privilege
- Check permissions server-side ALWAYS

### 2.4 OWASP Top 10 (انتبه لها دائماً)
- A01: Broken Access Control
- A02: Cryptographic Failures  
- A03: Injection
- A04: Insecure Design
- A05: Security Misconfiguration
- A06: Vulnerable Components
- A07: Auth Failures
- A08: Data Integrity Failures
- A09: Logging Failures
- A10: SSRF

### 2.5 Dependency Security (امن التبعيات)
- Use SCA tools (Snyk, Dependabot, OWASP)
- Keep dependencies updated
- Review changelogs for security patches
- Remove unused dependencies
- Pin critical dependencies

---

## 3. DevOps and Git Skills

### 3.1 Git Workflow (سير العمل مع Git)
- Use meaningful commit messages: "type: description"
- Keep commits atomic (one logical change)
- Use feature branches (not main direct commits)
- Rebase, do not merge, for feature branches
- Protect main/master branches
- Require PR reviews before merge

### 3.2 CI/CD Best Practices (افضل الممارسات)
- Build on every push
- Run ALL tests (unit, integration, e2e)
- Run linters and formatters
- Check code coverage (minimum 70%)
- Run security scans
- Deploy to staging before production
- Have rollback plan ready

### 3.3 Infrastructure as Code (البنية التحتية ككود)
- Version control all infrastructure
- Use idempotent configurations
- Separate state from configuration
- Test infrastructure before apply
- Document all changes

### 3.4 Docker Best Practices (الDocker)
- Use minimal base images (alpine, distroless)
- Do not run containers as root
- Use multi-stage builds
- Scan images for vulnerabilities
- Set resource limits
- Use health checks

---

## 4. QA and Testing Skills

### 4.1 Unit Testing (اختبارات الوحدة)
- Test one thing per test
- Use descriptive test names
- Follow AAA pattern (Arrange-Act-Assert)
- Test edge cases and boundaries
- Keep tests fast
- Tests should be idempotent
- Target coverage: Critical 80%+, Business 70%+

### 4.2 Integration Testing (اختبارات التكامل)
- Test component interactions
- Use test containers for databases
- Mock external services appropriately
- Test data setup and teardown
- Verify data integrity

### 4.3 End-to-End Testing (اختبارات النهاية)
- Test critical user flows only
- Use realistic test data
- Implement proper waits (not arbitrary sleeps)
- Take screenshots on failures
- Parallelize when possible

### 4.4 Performance Testing (اختبارات الاداء)
- Load Testing: behavior under expected load
- Stress Testing: beyond normal capacity
- Measure: Response Time (p50, p95, p99), Throughput, Error Rate

---

## 5. Code Quality Skills

### 5.1 Clean Code (الكود النظيف)
- Meaningful names for everything
- Small functions (Single Responsibility)
- Small classes
- DRY (Do not Repeat Yourself)
- Comments explain WHY, not WHAT
- Remove dead code immediately

### 5.2 SOLID Principles (مبادئ SOLID)
- S: Single Responsibility - سبب واحد للتغيير
- O: Open/Closed - مفتوح للامتداد مغلق للتعديل
- L: Liskov Substitution - قابل للاستبدال بالابن
- I: Interface Segregation - واجهات صغيرة متخصصة
- D: Dependency Inversion - الاعتماد على التجريد

### 5.3 Code Smells (روائح الكود)
- Long Method (>50 lines suspect)
- Large Class (>300 lines suspect)
- Duplicate Code - استخرج واعادة استخدام
- Dead Code - احذف فوراً
- Switch Statements - استخدم polymorphism

### 5.4 Refactoring (اعادة الهيكلة)
- Ensure tests exist first
- Make small incremental changes
- Do NOT change behavior while refactoring
- Test after each change
- Document why you refactored

---

## 6. Architecture and Design Skills

### 6.1 Design Patterns (انماط التصميم)
- Creational: Singleton, Factory, Builder, Prototype
- Structural: Adapter, Bridge, Composite, Decorator, Facade
- Behavioral: Observer, Strategy, Command, State, Template Method

### 6.2 System Design Concepts (مفاهيم التصميم)
- Scalability (horizontal vs vertical)
- Availability (redundancy, fail-over)
- Consistency (ACID vs BASE)
- Caching strategies
- Database sharding
- Load balancing

### 6.3 API Design (تصميم APIs)
- REST: nouns not verbs, proper HTTP methods
- Version your API
- Use proper status codes
- Implement pagination
- Return meaningful errors
- Document with OpenAPI/Swagger
- Secure with OAuth2/JWT

### 6.4 Database Design (تصميم قواعد البيانات)
- Normalization (1NF, 2NF, 3NF)
- Proper indexing (do not over-index)
- Use appropriate data types
- Constraints for data integrity
- Soft deletes for audit trails
- Migration strategies

---

## 7. Documentation Skills

### 7.1 Code Documentation (توثيق الكود)
- Document WHY, not WHAT
- Keep comments updated
- Document complex algorithms
- Document non-obvious decisions
- XML comments for public APIs

### 7.2 README Writing (كتابة README)
- Project description
- Features list
- Getting Started
- Prerequisites
- Installation steps
- Usage examples
- Contributing guidelines
- License

### 7.3 Architecture Decision Records (ADR)
- Document significant decisions
- Include: Context, Decision, Consequences
- Track status: Proposed, Accepted, Deprecated
- Use for future reference

---

## 8. Debugging Skills

### 8.1 Systematic Debugging (التصحيح المنظم)
1. Reproduce the issue consistently
2. Gather context (logs, environment)
3. Form hypothesis
4. Test hypothesis
5. If wrong, form new hypothesis
6. Fix root cause, not symptoms
7. Verify the fix
8. Look for similar issues

### 8.2 Logging Best Practices
- Log at appropriate levels
- Include context (request ID, user ID)
- Do NOT log secrets or passwords
- Structured logging for searchability
- Log errors with stack traces

### 8.3 Memory and Performance Debugging
- Use profilers to identify bottlenecks
- Check for memory leaks
- Monitor CPU usage
- Analyze database query performance
- Use caching appropriately

---

## 9. Claude-Specific Skills

### 9.1 Thinking Modes
- Extended Thinking: manual budget_tokens control
- Adaptive Thinking: Claude decides effort level
- Interleaved Thinking: think between tool calls

### 9.2 Claude Models for Agents
- Claude Opus 5.5: complex projects, agents, coding
- Claude Fable 5.1: demanding tasks, long-horizon
- Claude Sonnet 5: everyday tasks, cost-efficient
- Claude Haiku 4.5: fastest, lowest cost

### 9.3 Claude Best Practices
- Use system prompts effectively
- Leverage tool use capabilities
- Implement proper error handling for tool failures
- Use session management for context
- Cache prompts when possible

---

## 10. OpenAI-Specific Skills

### 10.1 Reasoning Effort Levels
- none: fast classification tasks
- low: data analysis, drafting, coding
- medium: agentic coding, research (DEFAULT)
- high: hard reasoning, deep planning
- xhigh: security review, enterprise tasks
- max: most complex tasks

### 10.2 Reasoning Modes
- standard: default execution
- pro: extra work for difficult tasks

### 10.3 OpenAI Best Practices
- Use Responses API over Chat Completions
- Preserve reasoning across calls
- Use configuration_update for mid-conversation
- Implement proper token management

---

## 11. GitHub Copilot Skills

### 11.1 Copilot Agent Mode
- Use for complex multi-file tasks
- Leverage for application scaffolding
- Combine with GitHub Actions
- Use MCP servers for external tools

### 11.2 Copilot Best Practices
- Write clear, specific prompts
- Use comments to guide suggestions
- Review ALL suggestions carefully
- Combine with manual code review
- Use inline explanations

---

## 12. Multi-Agent Skills

### 12.1 Agent Orchestration
- Define clear agent roles (Orchestrator, Planner, Coder)
- Implement proper communication
- Use shared state appropriately
- Handle agent failures gracefully
- Coordinate parallel execution

### 12.2 Multi-Agent Communication
- Define message protocols
- Implement error handling per agent
- Use transactional operations
- Log all agent interactions
- Monitor agent health

---

## 13. LLM Security Skills

### 13.1 Prompt Injection Prevention
- Validate and sanitize all inputs
- Use output validation
- Implement rate limiting
- Separate untrusted content
- Use input/output filters

### 13.2 LLM Security Best Practices
- Never expose system prompts
- Validate user inputs before processing
- Implement content filtering
- Use sandboxing for LLM outputs
- Monitor for unusual patterns

### 13.3 Secure Agentic Workflows
- Implement authorization checks
- Validate tool inputs
- Log all agent actions
- Implement rollback mechanisms
- Use least privilege for agents

---

## 14. General Best Practices

### 14.1 Before Writing Code
- Read existing code first
- Understand the architecture
- Check for similar solutions
- Plan your approach
- Write tests first (TDD)

### 14.2 While Writing Code
- Follow project conventions
- Keep functions small
- Use meaningful names
- Add comments where needed
- Test incrementally

### 14.3 After Writing Code
- Run all tests
- Run linters and formatters
- Check for security issues
- Update documentation
- Create/update tests if needed

### 14.4 Code Review Checklist
- Logic correctness
- Security vulnerabilities
- Performance implications
- Error handling
- Test coverage
- Documentation updates
- Code style adherence

---

## 15. Project-Specific Notes

### For Sayl POS Project:
- Framework: WinForms .NET 10
- Database: SQL Server with EF Core
- Architecture: Services + DTOs + ViewModels
- Use Result/Result<T> for all service returns
- NEVER expose DbContext to UI
- Use DI for all services
- Follow existing patterns
- RTL Arabic interface by default
- Multi-terminal support via LAN

---

## Sources

- OWASP Top 10: https://owasp.org/Top10/
- GitHub Skills: https://skills.github.com
- Secure Code Game: https://github.com/skills/secure-code-game
- Claude Docs: https://platform.claude.com/docs
- OpenAI Docs: https://platform.openai.com/docs
- SOLID Principles: Robert C. Martin

---

**Last Updated: September 2026**