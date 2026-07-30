import {
  deleteBusinessWorkflow,
  exportBusinessWorkflow,
  getBusinessWorkflow,
  listBusinessWorkflows,
} from '../api/businessWorkflows'
import SkillHome from './SkillHome'
import AdvancedSkillEditor from './AdvancedSkillEditor'
import SimpleSkillEditor from './SimpleSkillEditor'

/** Business Workflow 組裝點：YAML CRUD、simple/advanced editor 與唯讀 builtin view。 */
export default function BusinessWorkflowHome({ isAdmin }: { isAdmin: boolean }) {
  return (
    <SkillHome
      isAdmin={isAdmin}
      kind="flow"
      noun="業務流程"
      emptyLabel="尚無業務流程。"
      listCustom={listBusinessWorkflows}
      getDetail={getBusinessWorkflow}
      download={exportBusinessWorkflow}
      disable={deleteBusinessWorkflow}
      renderCustomEditor={(skill, controls) => (
        <AdvancedSkillEditor mode={{ kind: 'edit', name: skill.name }} initialDefinition={skill.definition} saved={skill} {...controls} />
      )}
      renderBuiltinEditor={(name, definition, controls) => (
        <AdvancedSkillEditor mode={{ kind: 'view', name }} initialDefinition={definition} {...controls} />
      )}
      renderCreateEditor={({ onAdvanced, ...controls }) => (
        <SimpleSkillEditor onAdvanced={onAdvanced} {...controls} />
      )}
      renderSimpleEditor={(initial, { onAdvanced, ...controls }) => (
        <SimpleSkillEditor initial={initial} onAdvanced={onAdvanced} {...controls} />
      )}
      renderAdvancedEditor={(definition, controls) => (
        <AdvancedSkillEditor mode={{ kind: 'create' }} initialDefinition={definition} {...controls} />
      )}
    />
  )
}
