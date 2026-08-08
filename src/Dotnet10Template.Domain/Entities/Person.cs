using System;
using System.Collections.Generic;
using System.Text;

namespace Dotnet10Template.Domain.Entities
{
    public sealed class Person
    {
        public int Id { get; set; }

        public required string Name { get; set; }
    }
}
