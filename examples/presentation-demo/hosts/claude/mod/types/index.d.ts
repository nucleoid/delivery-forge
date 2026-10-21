declare module 'claude-code' {
  interface PluginState {
    'forge-cockpit-demo': {
      view: 'overview' | 'evidence' | 'usage' | 'activity'
      scenario: string
      selected: number
      detail: boolean
      notice: string
    }
  }
}
