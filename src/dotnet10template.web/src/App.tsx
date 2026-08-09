import { useEffect, useState } from 'react'
import './App.css'

function App() {
    const [message, setMessage] = useState('Loading...')
    const [error, setError] = useState<string | null>(null)

    useEffect(() => {
        const loadGreeting = async () => {
            try {
                const response = await fetch('/api/hello')

                if (!response.ok) {
                    throw new Error(`API returned ${response.status}`)
                }

                const greeting = await response.text()

                setMessage(greeting)
            } catch (err) {
                console.error(err)

                setError('Unable to load greeting from the API.')
            }
        }

        void loadGreeting()
    }, [])

    return (
        <main>
            <h1>Dotnet10Template</h1>

            {error ? (
                <p>{error}</p>
            ) : (
                <p>{message}</p>
            )}
        </main>
    )
}

export default App