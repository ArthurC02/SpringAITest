import { deleteSkill, exportSkill, getSkill, importSkill, listSkills } from '../api/skills'
import SkillHome from './SkillHome'
import AgentSkillEditor from './AgentSkillEditor'
import BuiltinAgentSkillView from './BuiltinAgentSkillView'

async function uploadAgentSkill(file: File) {
  await importSkill(file, file.name)
}

/** Agent Skill 組裝點：package import/export/editor；不承載 YAML/simple editor。 */
export default function AgentSkillHome({ isAdmin }: { isAdmin: boolean }) {
  return (
    <SkillHome
      isAdmin={isAdmin}
      kind="agentic"
      noun="Agent Skill"
      emptyLabel="尚無 Agent Skill。"
      listCustom={listSkills}
      getDetail={getSkill}
      download={exportSkill}
      disable={deleteSkill}
      upload={uploadAgentSkill}
      renderCustomEditor={(skill, controls) => (
        <AgentSkillEditor name={skill.name} {...controls} />
      )}
      renderBuiltinEditor={(name, definition, controls) => (
        <BuiltinAgentSkillView name={name} definition={definition} onClose={controls.onSaved} />
      )}
    />
  )
}
