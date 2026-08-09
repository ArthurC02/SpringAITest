interface Props {
  name: string
  definition: string
  onClose: () => void
}

/** Read-only catalog view for built-in Agent Skills; built-ins are never package-edit targets. */
export default function BuiltinAgentSkillView({ name, definition, onClose }: Props) {
  return (
    <section className="skill-editor" aria-label={`內建 Agent Skill ${name}`}>
      <div className="skill-editor__head">
        <h3 className="skill-editor__title">
          {name} <span className="badge badge--user">內建 Agent Skill</span>
        </h3>
        <div className="skill-editor__actions">
          <button className="btn" type="button" onClick={onClose}>返回 Agent Skills</button>
        </div>
      </div>
      <p className="muted">內建 Agent Skill 為唯讀，如需建立可自行修改的技能，請匯入套件建立租戶自有版本。</p>
      <div className="field">
        <label htmlFor="builtin-agent-skill-definition">SKILL.md</label>
        <textarea
          id="builtin-agent-skill-definition"
          className="textarea"
          rows={18}
          value={definition}
          readOnly
        />
      </div>
    </section>
  )
}
