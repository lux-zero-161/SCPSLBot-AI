using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SCPSLBot.AI.FirstPersonControl.Mind.Escape
{
    internal class PlayerEscaped : Belief<bool>
    {
        public bool IsEscaped { get; private set; }

        public void MarkEscaped()
        {
            if (IsEscaped)
            {
                return;
            }

            IsEscaped = true;
            InvokeOnUpdate();
        }

        public override string ToString()
        {
            return $"{nameof(PlayerEscaped)}: {IsEscaped}";
        }
    }
}
