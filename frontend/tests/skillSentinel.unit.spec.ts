import { describe, expect, it } from 'vitest'
import { skillSourceLabel, stripSkillSentinel } from '../src/skillSentinel'

describe('stripSkillSentinel', () => {
  it('strips a trailing sentinel and returns the skill name', () => {
    expect(stripSkillSentinel('這是答案\n<!--skill:rag-qa-->')).toEqual({
      content: '這是答案',
      skillName: 'rag-qa',
    })
  })

  it('leaves content untouched when there is no sentinel', () => {
    expect(stripSkillSentinel('一般聊天回覆')).toEqual({
      content: '一般聊天回覆',
      skillName: null,
    })
  })

  it('fails open on a malformed sentinel (wrong charset/shape)', () => {
    const malformed = '答案\n<!--skill:RAG_QA-->'
    expect(stripSkillSentinel(malformed)).toEqual({ content: malformed, skillName: null })
  })

  it('does not strip when the marker-like text appears mid-content', () => {
    const midContent = '前面提到 <!--skill:kb-query--> 這件事之後還有更多內容'
    expect(stripSkillSentinel(midContent)).toEqual({ content: midContent, skillName: null })
  })
})

describe('skillSourceLabel', () => {
  it('maps knowledge-base skills to a shared label', () => {
    expect(skillSourceLabel('rag-qa')).toBe('來源:知識庫')
    expect(skillSourceLabel('kb-query')).toBe('來源:知識庫')
  })

  it('falls back to the raw skill name otherwise', () => {
    expect(skillSourceLabel('weather-lookup')).toBe('來源:weather-lookup')
  })
})
