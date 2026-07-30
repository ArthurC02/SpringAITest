interface Props {
  name: string
  definition: string
  onClose: () => void
}

/** Read-only catalog view for built-in Agent Skills; built-ins are never package-edit targets. */
export default function BuiltinAgentSkillView({ name, definition, onClose }: Props) {
  return (
    <section className="skill-editor" aria-label={`Built-in Agent Skill ${name}`}>
      <div className="skill-editor__head">
        <h3 className="skill-editor__title">
          {name} <span className="badge badge--user">Built-in Agent Skill</span>
        </h3>
        <div className="skill-editor__actions">
          <button className="btn" type="button" onClick={onClose}>返回 Agent Skills</button>
        </div>
      </div>
      <p className="muted">Built-in Agent Skills are read-only. Import a package to create a tenant-owned skill.</p>
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
